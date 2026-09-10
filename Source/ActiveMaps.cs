using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
    /// <summary>
    /// Public facade: open a tile map if needed and pin it in memory without a scout pawn.
    /// </summary>
    public static class ActiveMaps
    {
        public const string SiteDefName = "Crystallize_ActiveMapSite";

        public static Map EnsureLoadedMap(PlanetTile tile, ActiveMapReason reason = ActiveMapReason.Other)
        {
            if (!tile.Valid) return null;
            try
            {
                Map map = Current.Game?.FindMap(tile);
                if (map != null) return map;

                WorldObjectDef siteDef = DefDatabase<WorldObjectDef>.GetNamedSilentFail(SiteDefName);
                MapParent existing = Find.WorldObjects?.MapParentAt(tile);
                WorldObjectDef suggested = existing != null ? null : siteDef;
                if (existing == null && siteDef == null)
                {
                    Log.Warning("[CrystallizeActiveMaps] EnsureLoadedMap: missing Crystallize_ActiveMapSite def.");
                    return null;
                }

                map = GetOrGenerateMapUtility.GetOrGenerateMap(tile, suggested);
                return map;
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] EnsureLoadedMap failed: {e}");
                return null;
            }
        }

        public static void Pin(Map map, string token, ActiveMapReason reason = ActiveMapReason.Other)
        {
            if (map == null || token.NullOrEmpty()) return;
            Find.World?.GetComponent<WorldComponent_ActiveMaps>()?.Pin(map, token, reason);
        }

        public static void Unpin(Map map, string token)
        {
            if (map == null || token.NullOrEmpty()) return;
            Find.World?.GetComponent<WorldComponent_ActiveMaps>()?.Unpin(map, token);
        }

        /// <summary>
        /// Tokens owned by Active Maps Framework / its strike consumers.
        /// Unload may drop these; any other token is treated as foreign and blocks hard unload.
        /// </summary>
        public static readonly string[] OwnedPinTokens =
        {
            RailgunStrikeZone.PinTokenMission,
            RailgunStrikeZone.PinTokenLoot,
            OutpostStrikeZone.PinToken,
            WorldComponent_StrikeAftermath.PinHoldMap,
            WorldComponent_StrikeAftermath.PinHibernateMap,
            WorldComponent_StrikeAftermath.PinLinkedFloors,
            AbstractOutpost.PinRaid,
        };

        public static void UnpinOwned(Map map)
        {
            if (map == null) return;
            for (int i = 0; i < OwnedPinTokens.Length; i++)
                Unpin(map, OwnedPinTokens[i]);
        }

        public static bool IsOwnedPinToken(string token)
        {
            if (token.NullOrEmpty()) return false;
            for (int i = 0; i < OwnedPinTokens.Length; i++)
            {
                if (OwnedPinTokens[i] == token) return true;
            }
            return false;
        }

        /// <summary>True if any pin token remains that we did not author.</summary>
        public static bool HasForeignPins(Map map)
        {
            if (map == null) return false;
            return Find.World?.GetComponent<WorldComponent_ActiveMaps>()?.HasForeignPins(map) == true;
        }

        public static string DescribePins(Map map)
        {
            if (map == null) return "(null map)";
            return Find.World?.GetComponent<WorldComponent_ActiveMaps>()?.DescribePins(map) ?? "(no wc)";
        }

        public static bool IsPinned(Map map)
        {
            if (map == null) return false;
            return Find.World?.GetComponent<WorldComponent_ActiveMaps>()?.IsPinned(map) == true;
        }

        public static bool IsOurTempSite(MapParent parent)
        {
            if (parent == null) return false;
            if (parent is ActiveMapSite) return true;
            if (parent is AbstractOutpost) return true;
            return parent.def?.defName == SiteDefName;
        }

        public static void JumpToMap(Map map)
        {
            if (map == null) return;
            try
            {
                Current.Game.CurrentMap = map;
                CameraJumper.TryHideWorld();
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] JumpToMap: {e.Message}");
            }
        }
    }

    public class WorldComponent_ActiveMaps : WorldComponent
    {
        private readonly Dictionary<int, HashSet<string>> pinsByMapId = new Dictionary<int, HashSet<string>>();

        public WorldComponent_ActiveMaps(World world) : base(world) { }

        public void Pin(Map map, string token, ActiveMapReason reason)
        {
            if (map == null || token.NullOrEmpty()) return;
            int id = map.uniqueID;
            if (!pinsByMapId.TryGetValue(id, out HashSet<string> set))
            {
                set = new HashSet<string>();
                pinsByMapId[id] = set;
            }
            set.Add(token);
        }

        public void Unpin(Map map, string token)
        {
            if (map == null || token.NullOrEmpty()) return;
            int id = map.uniqueID;
            if (!pinsByMapId.TryGetValue(id, out HashSet<string> set)) return;
            set.Remove(token);
            if (set.Count == 0)
                pinsByMapId.Remove(id);
        }

        public bool IsPinned(Map map)
        {
            if (map == null) return false;
            return pinsByMapId.TryGetValue(map.uniqueID, out HashSet<string> set) && set.Count > 0;
        }

        public bool HasForeignPins(Map map)
        {
            if (map == null) return false;
            if (!pinsByMapId.TryGetValue(map.uniqueID, out HashSet<string> set) || set.Count == 0)
                return false;
            foreach (string t in set)
            {
                if (!ActiveMaps.IsOwnedPinToken(t))
                    return true;
            }
            return false;
        }

        public string DescribePins(Map map)
        {
            if (map == null) return "(null)";
            if (!pinsByMapId.TryGetValue(map.uniqueID, out HashSet<string> set) || set.Count == 0)
                return "none";
            return string.Join(", ", set);
        }

        /// <summary>Drop all tokens for a map id (map gone / force clear).</summary>
        public void ClearPinsForMapId(int mapUniqueId)
        {
            pinsByMapId.Remove(mapUniqueId);
        }

        /// <summary>
        /// Remove pin entries whose map no longer exists in Find.Maps.
        /// Prevents ghost uniqueIDs after Destroy/Deinit without Unpin.
        /// </summary>
        public int PruneStalePins()
        {
            if (pinsByMapId.Count == 0) return 0;
            HashSet<int> live = null;
            if (Find.Maps != null)
            {
                live = new HashSet<int>();
                for (int i = 0; i < Find.Maps.Count; i++)
                {
                    Map m = Find.Maps[i];
                    if (m != null) live.Add(m.uniqueID);
                }
            }

            List<int> stale = null;
            foreach (int id in pinsByMapId.Keys)
            {
                if (live == null || !live.Contains(id))
                {
                    if (stale == null) stale = new List<int>();
                    stale.Add(id);
                }
            }
            if (stale == null) return 0;
            for (int i = 0; i < stale.Count; i++)
                pinsByMapId.Remove(stale[i]);
            return stale.Count;
        }

        public override void WorldComponentTick()
        {
            // Rare sweep — cheap if dictionary empty / no ghosts.
            if (Find.TickManager.TicksGame % 5000 != 0) return;
            int n = PruneStalePins();
            if (n > 0)
                Log.Message($"[CrystallizeActiveMaps] Pruned {n} stale map pin id(s).");
        }

        // Orphan DestroyedSettlement purge lives on WorldComponent_StrikeAftermath (after salvage
        // entries load) — do not run it from here (load-order race).

        public override void ExposeData()
        {
            base.ExposeData();
            List<int> mapIds = null;
            List<string> flatTokens = null;
            List<int> counts = null;

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                PruneStalePins();
                mapIds = pinsByMapId.Keys.ToList();
                counts = new List<int>();
                flatTokens = new List<string>();
                for (int i = 0; i < mapIds.Count; i++)
                {
                    HashSet<string> set = pinsByMapId[mapIds[i]];
                    counts.Add(set.Count);
                    foreach (string t in set)
                        flatTokens.Add(t);
                }
            }

            Scribe_Collections.Look(ref mapIds, "crystallizeAmMapIds", LookMode.Value);
            Scribe_Collections.Look(ref counts, "crystallizeAmCounts", LookMode.Value);
            Scribe_Collections.Look(ref flatTokens, "crystallizeAmTokens", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                pinsByMapId.Clear();
                if (mapIds == null || counts == null || flatTokens == null) return;
                int ti = 0;
                for (int i = 0; i < mapIds.Count && i < counts.Count; i++)
                {
                    var set = new HashSet<string>();
                    int n = counts[i];
                    for (int j = 0; j < n && ti < flatTokens.Count; j++, ti++)
                        set.Add(flatTokens[ti]);
                    if (set.Count > 0)
                        pinsByMapId[mapIds[i]] = set;
                }
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // Maps are available; drop ghosts from older saves.
                PruneStalePins();
            }
        }
    }
}
