using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Crystallize.ActiveMaps
{
    public enum StrikeZoneKind
    {
        Ours,
        EnemySettlement,
        NeutralSite
    }

    public enum RailgunStrikeEndReason
    {
        Manual,
        Retarget,
        GunsGone
    }

    /// <summary>
    /// One active Rimatomics railgun strike zone. Map stay-alive via ActiveMaps.Pin (no pawn).
    /// </summary>
    public static class RailgunStrikeZone
    {
        public const int LootWindowTicks = 240000; // 4 days
        public const string PinTokenMission = "rimatomics.firemission";
        public const string PinTokenLoot = "rimatomics.loot";

        public static bool Active { get; private set; }
        public static PlanetTile Tile { get; private set; } = PlanetTile.Invalid;
        public static StrikeZoneKind Kind { get; private set; }
        public static int MapUniqueId { get; private set; } = -1;

        /// <summary>Thing IDs of CompCauseGameCondition buildings seen when the zone opened / refreshed.</summary>
        private static readonly HashSet<int> TrackedConditionCauserIds = new HashSet<int>();
        private static bool HadConditionCausers;
        private static bool AnyShotFired;
        /// <summary>True after mid-strike (or EndZone) kill of site conditions — avoid repeating work.</summary>
        private static bool ConditionsCleared;

        private static Type RailgunType;
        private static Type EnergyWeaponType;
        private static FieldInfo LongTargetField;
        private static MethodInfo ResetForcedTargetMethod;
        private static bool Resolved;

        public static void ExposeData()
        {
            bool active = Active;
            int tileId = Active && Tile.Valid ? (int)Tile : -1;
            int kind = (int)Kind;
            int mapId = MapUniqueId;
            bool hadCausers = HadConditionCausers;
            bool anyShot = AnyShotFired;
            bool conditionsCleared = ConditionsCleared;
            List<int> causerIds = TrackedConditionCauserIds.ToList();
            Scribe_Values.Look(ref active, "crystallizeAmStrikeActive", false);
            Scribe_Values.Look(ref tileId, "crystallizeAmStrikeTile", -1);
            Scribe_Values.Look(ref kind, "crystallizeAmStrikeKind", 0);
            Scribe_Values.Look(ref mapId, "crystallizeAmStrikeMapId", -1);
            Scribe_Values.Look(ref hadCausers, "crystallizeAmStrikeHadCausers", false);
            Scribe_Values.Look(ref anyShot, "crystallizeAmStrikeAnyShot", false);
            Scribe_Values.Look(ref conditionsCleared, "crystallizeAmStrikeConditionsCleared", false);
            Scribe_Collections.Look(ref causerIds, "crystallizeAmStrikeCauserIds", LookMode.Value);
            Active = active;
            Tile = tileId >= 0 ? (PlanetTile)tileId : PlanetTile.Invalid;
            Kind = (StrikeZoneKind)kind;
            MapUniqueId = mapId;
            HadConditionCausers = hadCausers;
            AnyShotFired = anyShot;
            ConditionsCleared = conditionsCleared;
            TrackedConditionCauserIds.Clear();
            if (causerIds != null)
            {
                for (int i = 0; i < causerIds.Count; i++)
                    TrackedConditionCauserIds.Add(causerIds[i]);
            }
        }

		public static void NotifyShotFiredAtTile(PlanetTile tile)
		{
			if (!Active || !Tile.Valid) return;
			if (tile != Tile) return;
			AnyShotFired = true;
			Map map = FindMap(MapUniqueId, Tile);
			if (map != null)
			{
				WorldComponent_StrikeAftermath.MarkShelled(map);
				SnapshotConditionCausersMerge(map);
				StrikeHostilityUtility.NotifyPlayerAttackedMap(map);
			}
			else if (Tile.Valid)
			{
				MapParent parent = Find.WorldObjects?.MapParentAt(Tile);
				WorldComponent_StrikeAftermath.MarkShelled(parent);
				StrikeHostilityUtility.NotifyPlayerAttackedParent(parent);
			}
		}

        public static bool IsActiveMap(Map map)
        {
            if (!Active || map == null) return false;
            if (MapUniqueId >= 0 && map.uniqueID == MapUniqueId) return true;
            return Tile.Valid && map.Tile == Tile;
        }

        public static bool EnsureMapReady(PlanetTile tile, out string error)
        {
            error = null;
            try
            {
                if (MapHibernation.ForbidStrikeOnFragile
                    && MapHibernation.IsFragileMapBoundTile(tile))
                {
                    error = "Crystallize_AM_FragileForbidStrike".Translate();
                    return false;
                }

                ResolveRimatomics();
                Map map = ActiveMaps.EnsureLoadedMap(tile, ActiveMapReason.FireMission);
                if (map == null)
                {
                    error = "Crystallize_AM_MapFailed".Translate();
                    return false;
                }

                MapParent parent = map.Parent;
                StrikeZoneKind kind = Classify(parent);

                bool newZone = !Active || !Tile.Valid || Tile != tile;
                Active = true;
                InvalidateRailgunTargetCache();
                FireMissionUi.InvalidateStrikeGizmoCache();
                Tile = tile;
                Kind = kind;
                MapUniqueId = map.uniqueID;
                ActiveMaps.Pin(map, PinTokenMission, ActiveMapReason.FireMission);
                if (newZone)
                {
                    AnyShotFired = false;
                    ConditionsCleared = false;
                    SnapshotConditionCausers(map);
                    StrikeHostilityUtility.ClearSession();
                }
                else
                    SnapshotConditionCausersMerge(map);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] EnsureMapReady failed: {e}");
                error = "Crystallize_AM_MapFailed".Translate();
                return false;
            }
        }

        private static bool endingZone;

        public static void EndZone(RailgunStrikeEndReason reason)
        {
            // Re-entrancy: ClearAll / ResetForcedTarget / nested UI must not run EndZone twice.
            if (endingZone) return;
            endingZone = true;
            try
            {
                EndZoneInner(reason);
            }
            finally
            {
                endingZone = false;
            }
        }

        private static void EndZoneInner(RailgunStrikeEndReason reason)
        {
            // Allow pressing End from the railgun even if Active was already cleared
            // but Rimatomics longTarget / loot window are still lingering.
            bool zoneWasActive = Active;
            if (!zoneWasActive && !AnyRailgunHasWorldTarget() && !AnyLootWindowActive())
                return;

            PlanetTile tile = Tile;
            StrikeZoneKind kind = Kind;
            int mapId = MapUniqueId;

            Map map = ResolveStrikeMap(mapId, tile);
            // Always full Rimatomics reset (same as CommandStopForceAttack), not only when longTarget reads valid.
            ClearAllRailgunWorldTargets();

            if (map != null)
            {
                ActiveMaps.Unpin(map, PinTokenMission);
                ActiveMaps.Unpin(map, PinTokenLoot);
            }

            Active = false;
            InvalidateRailgunTargetCache();
            FireMissionUi.InvalidateStrikeGizmoCache();
            Tile = PlanetTile.Invalid;
            MapUniqueId = -1;

            if (zoneWasActive)
            {
                if (map == null || map.Parent == null || map.Parent.Destroyed)
                {
                    EndTrackedGameConditions(map);
                    ClearCauserTracking();
                }
                else
                {
                    try
                    {
                        ApplyCleanup(map, kind);
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"[CrystallizeActiveMaps] EndZone cleanup failed: {e}");
                    }
                    finally
                    {
                        ClearCauserTracking();
                    }
                }
            }
            // Do NOT ForceFinish loot windows here. A second End used to instantly destroy
            // camps right after the first End started the 4-day salvage hold.

            JumpHomeAfterEnd(map);
            Messages.Message("Crystallize_AM_StrikeEnded".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>True while the player still needs an End control (active zone or lingering railgun targets).</summary>
        public static bool NeedsEndControl()
        {
            // Loot/salvage hold alone does not need End — the timer removes the site.
            // Showing End during loot made a second press feel like "instant delete".
            if (Active) return true;
            return AnyRailgunHasWorldTargetCached();
        }

        /// <summary>
        /// Tick-throttled railgun world-target scan. Full AllThings walk must not run every GUI frame.
        /// </summary>
        const int RailgunTargetCacheInterval = 15;
        static int railgunTargetCacheTick = int.MinValue;
        static bool railgunTargetCache;

        public static bool AnyRailgunHasWorldTargetCached()
        {
            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (railgunTargetCacheTick != int.MinValue
                && tick - railgunTargetCacheTick < RailgunTargetCacheInterval
                && tick >= railgunTargetCacheTick)
                return railgunTargetCache;
            railgunTargetCacheTick = tick;
            railgunTargetCache = AnyRailgunHasWorldTarget();
            return railgunTargetCache;
        }

        /// <summary>Invalidate GUI/throttle cache when zone state changes.</summary>
        public static void InvalidateRailgunTargetCache()
        {
            railgunTargetCacheTick = int.MinValue;
        }

        public static bool AnyLootWindowActive()
        {
            if (Find.Maps == null) return false;
            for (int i = 0; i < Find.Maps.Count; i++)
            {
                MapComponent_StrikeLootWindow loot = Find.Maps[i]?.GetComponent<MapComponent_StrikeLootWindow>();
                if (loot != null && loot.Active) return true;
            }
            return false;
        }

        public static bool AnyRailgunHasWorldTarget()
        {
            ResolveRimatomics();
            if (RailgunType == null || LongTargetField == null || Find.Maps == null) return false;
            foreach (Map map in Find.Maps)
            {
                if (map?.listerThings == null) continue;
                List<Thing> things = map.listerThings.AllThings;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t == null || !RailgunType.IsInstanceOfType(t)) continue;
                    if (TryReadLongTarget(t, out GlobalTargetInfo gti) && gti.IsValid)
                        return true;
                }
            }
            return false;
        }

        private static void JumpHomeAfterEnd(Map strikeMap)
        {
            try
            {
                Map home = Find.AnyPlayerHomeMap;
                if (home == null) return;
                if (Find.CurrentMap != null && Find.CurrentMap != strikeMap && Find.CurrentMap.IsPlayerHome)
                    return;
                Current.Game.CurrentMap = home;
                CameraJumper.TryJump(home.Center, home);
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] JumpHomeAfterEnd: {e.Message}");
            }
        }

        private static void ForceFinishAllLootWindows()
        {
            if (Find.Maps == null) return;
            // Copy list — FinalizeCleanup removes maps.
            List<Map> maps = Find.Maps.ToList();
            for (int i = 0; i < maps.Count; i++)
            {
                MapComponent_StrikeLootWindow loot = maps[i]?.GetComponent<MapComponent_StrikeLootWindow>();
                if (loot == null || !loot.Active) continue;
                try { loot.ForceFinishNow(); }
                catch (Exception e) { Log.Warning($"[CrystallizeActiveMaps] ForceFinish loot: {e.Message}"); }
            }
        }

		private static void ApplyCleanup(Map map, StrikeZoneKind kind)
		{
			if (map == null) return;

			bool generatorsNeutralized = ConditionGeneratorsNeutralized(map);
			MapParent parent = map.Parent;

			// Keep mid-strike condition teardown for sites that declared causers.
			Action<Map, MapParent> clear = null;
			if (kind == StrikeZoneKind.NeutralSite
			    || (generatorsNeutralized && kind != StrikeZoneKind.EnemySettlement))
			{
				clear = (m, p) =>
				{
					if (SiteDeclaresConditionCauser(p) || HadConditionCausers
					    || TrackedConditionCauserIds.Count > 0 || generatorsNeutralized)
						ClearStrikeConditions(m, p, notify: false);
				};
			}

			StrikeMapAftermath.OnEndStrike(map, new StrikeEndContext
			{
				AnyShotFired = AnyShotFired,
				PinTokenMission = PinTokenMission,
				PinTokenLoot = PinTokenLoot,
				ClearConditions = clear
			});
		}

        private static void SnapshotConditionCausers(Map map)
        {
            TrackedConditionCauserIds.Clear();
            HadConditionCausers = false;
            SnapshotConditionCausersMerge(map);
        }

        private static void SnapshotConditionCausersMerge(Map map)
        {
            if (map?.listerThings == null) return;
            List<Thing> causers = map.listerThings.ThingsInGroup(ThingRequestGroup.ConditionCauser);
            for (int i = 0; i < causers.Count; i++)
            {
                Thing t = causers[i];
                if (t == null || t.Destroyed) continue;
                TrackedConditionCauserIds.Add(t.thingIDNumber);
                HadConditionCausers = true;
            }
        }

        private static bool ConditionGeneratorsNeutralized(Map map)
        {
            // Only trust causers we actually saw on the strike map.
            // SiteDeclaresConditionCauser alone is NOT enough: at zone open the site part
            // always declares a toxifier/etc., but GenStep may not have placed it yet (or
            // failed to). Treating that as "neutralized" called ClearStrikeConditions →
            // DestroyHiddenSiteConditionCausers wiped the generator and showed
            // Crystallize_AM_ConditionCleared before any shot.
            bool sawMapCausers = HadConditionCausers || TrackedConditionCauserIds.Count > 0;
            if (!sawMapCausers)
                return AnyDestroyedCauserConditionFromMap(map);

            if (map?.listerThings == null)
                return true;

            List<Thing> living = map.listerThings.ThingsInGroup(ThingRequestGroup.ConditionCauser);
            for (int i = 0; i < living.Count; i++)
            {
                Thing t = living[i];
                if (t == null || t.Destroyed) continue;
                return false;
            }
            return true;
        }

        private static bool SiteDeclaresConditionCauser(MapParent parent)
        {
            if (!(parent is Site site) || site.parts == null) return false;
            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                if (part?.def?.conditionCauserDef != null)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Mod/vanilla edge case: causer destroyed but GameCondition still active (site kept it alive).
        /// </summary>
        private static bool AnyDestroyedCauserConditionFromMap(Map map)
        {
            if (map == null) return false;
            if (HasDestroyedCauserCondition(Find.World?.gameConditionManager, map))
                return true;
            if (Find.Maps == null) return false;
            for (int i = 0; i < Find.Maps.Count; i++)
            {
                if (HasDestroyedCauserCondition(Find.Maps[i]?.gameConditionManager, map))
                    return true;
            }
            return false;
        }

        private static bool HasDestroyedCauserCondition(GameConditionManager manager, Map strikeMap)
        {
            if (manager?.ActiveConditions == null) return false;
            for (int i = 0; i < manager.ActiveConditions.Count; i++)
            {
                GameCondition c = manager.ActiveConditions[i];
                Thing causer = c?.conditionCauser;
                if (causer == null) continue;
                if (!causer.Destroyed) continue;
                if (TrackedConditionCauserIds.Contains(causer.thingIDNumber))
                    return true;
                // Destroyed Thing often has Map==null; MapHeld may still point at strike map briefly.
                if (causer.MapHeld == strikeMap)
                    return true;
            }
            return false;
        }

        private static void EndTrackedGameConditions(Map strikeMap)
        {
            try
            {
                EndConditionsInManager(Find.World?.gameConditionManager, strikeMap);
                if (Find.Maps == null) return;
                for (int i = 0; i < Find.Maps.Count; i++)
                    EndConditionsInManager(Find.Maps[i]?.gameConditionManager, strikeMap);
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] EndTrackedGameConditions: {e.Message}");
            }
        }

        private static void EndConditionsInManager(GameConditionManager manager, Map strikeMap)
        {
            if (manager?.ActiveConditions == null) return;
            List<GameCondition> copy = manager.ActiveConditions.ToList();
            for (int i = 0; i < copy.Count; i++)
            {
                GameCondition c = copy[i];
                Thing causer = c?.conditionCauser;
                if (causer == null) continue;
                bool tracked = TrackedConditionCauserIds.Contains(causer.thingIDNumber);
                bool fromStrike = strikeMap != null
                    && (causer.Map == strikeMap || causer.MapHeld == strikeMap
                        || (causer.Destroyed && tracked));
                if (!tracked && !fromStrike) continue;
                try { c.End(); }
                catch (Exception e) { Log.Warning($"[CrystallizeActiveMaps] GameCondition.End failed: {e.Message}"); }
            }
        }

        private static void RemoveMapAndWorldObject(Map map, MapParent parent, bool forceDestroySite)
        {
            try
            {
                if (map != null)
                    Current.Game.DeinitAndRemoveMap(map, notifyPlayer: false);
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] DeinitAndRemoveMap: {e.Message}");
            }

            if (!forceDestroySite || parent == null || parent.Destroyed) return;
            // Settlements: only remove if already converted / not a living faction base — caller gates this.
            if (parent is Settlement) return;
            try { parent.Destroy(); }
            catch (Exception e) { Log.Warning($"[CrystallizeActiveMaps] Destroy site: {e.Message}"); }
        }

        private static void ClearCauserTracking()
        {
            TrackedConditionCauserIds.Clear();
            HadConditionCausers = false;
            AnyShotFired = false;
            ConditionsCleared = false;
            StrikeHostilityUtility.ClearSession();
        }

        /// <summary>
        /// True if this thing is (or was) a condition-causer relevant to the active strike.
        /// Called from Destroy Prefix while the Thing is still intact.
        /// </summary>
        public static bool IsRelevantConditionCauser(Thing thing)
        {
            if (!Active || thing == null) return false;
            if (TrackedConditionCauserIds.Contains(thing.thingIDNumber)) return true;
            if (thing.def?.GetCompProperties<CompProperties_CausesGameCondition>() == null) return false;
            Map map = thing.Map ?? thing.MapHeld;
            return IsActiveMap(map);
        }

        /// <summary>
        /// End fallout / site conditions as soon as map generators are gone — without ending the fire mission.
        /// Cheap: no-op if already cleared or generators still alive.
        /// Requires at least one railgun shot so we never fake-clear on map open.
        /// </summary>
        public static void TryClearConditionsIfGeneratorsGone(bool notify)
        {
            if (!Active || ConditionsCleared || !AnyShotFired) return;
            Map map = FindMap(MapUniqueId, Tile);
            if (map == null) return;
            // Late merge: first ticks after open may miss ConditionCauser registration.
            SnapshotConditionCausersMerge(map);
            if (!ConditionGeneratorsNeutralized(map)) return;
            ClearStrikeConditions(map, map.Parent, notify);
        }

        private static void ClearStrikeConditions(Map map, MapParent parent, bool notify)
        {
            if (ConditionsCleared) return;
            try
            {
                DestroyHiddenSiteConditionCausers(parent);
                EndSiteConditionDefs(parent);
                EndTrackedGameConditions(map);
                ConditionsCleared = true;
                if (notify)
                {
                    Messages.Message(
                        "Crystallize_AM_ConditionCleared".Translate(),
                        MessageTypeDefOf.PositiveEvent,
                        historical: false);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] ClearStrikeConditions: {e.Message}");
            }
        }

        /// <summary>
        /// SitePartWorker_ConditionCauser keeps an unspawned conditionCauser and DoTick()s it,
        /// which forever refreshes GameCondition.TicksLeft. Destroying only the map building is not enough.
        /// </summary>
        private static void DestroyHiddenSiteConditionCausers(MapParent parent)
        {
            if (!(parent is Site site) || site.parts == null) return;
            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                Thing causer = part?.conditionCauser;
                if (causer == null || causer.Destroyed) continue;
                try
                {
                    // Same Thing is GenStep-spawned onto the map. Never KillFinalize a living
                    // map building from this path — only vanish the SitePart's unspawned tick
                    // extender (or a already-despawned leftover).
                    if (causer.Spawned)
                        continue;
                    causer.Destroy(DestroyMode.Vanish);
                }
                catch (Exception e)
                {
                    Log.Warning($"[CrystallizeActiveMaps] Destroy site conditionCauser: {e.Message}");
                }
            }
        }

        private static void EndSiteConditionDefs(MapParent parent)
        {
            if (!(parent is Site site) || site.parts == null) return;
            var defs = new HashSet<GameConditionDef>();
            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                ThingDef causerDef = part?.def?.conditionCauserDef;
                if (causerDef == null) continue;
                CompProperties_CausesGameCondition props =
                    causerDef.GetCompProperties<CompProperties_CausesGameCondition>();
                if (props?.conditionDef != null)
                    defs.Add(props.conditionDef);
            }
            if (defs.Count == 0) return;

            EndConditionsMatchingDefs(Find.World?.gameConditionManager, defs);
            if (Find.Maps == null) return;
            for (int i = 0; i < Find.Maps.Count; i++)
                EndConditionsMatchingDefs(Find.Maps[i]?.gameConditionManager, defs);
        }

        private static void EndConditionsMatchingDefs(GameConditionManager manager, HashSet<GameConditionDef> defs)
        {
            if (manager?.ActiveConditions == null || defs == null) return;
            List<GameCondition> copy = manager.ActiveConditions.ToList();
            for (int i = 0; i < copy.Count; i++)
            {
                GameCondition c = copy[i];
                if (c?.def == null || !defs.Contains(c.def)) continue;
                try { c.End(); }
                catch (Exception e) { Log.Warning($"[CrystallizeActiveMaps] End condition by def: {e.Message}"); }
            }
        }

        private static void StartLootWindow(Map map, bool removeWorldObjectWhenDone)
        {
            MapComponent_StrikeLootWindow loot = map.GetComponent<MapComponent_StrikeLootWindow>();
            if (loot == null)
            {
                loot = new MapComponent_StrikeLootWindow(map);
                map.components.Add(loot);
            }
            ActiveMaps.Pin(map, PinTokenLoot, ActiveMapReason.LootWindow);
            loot.Begin(LootWindowTicks, removeWorldObjectWhenDone);
        }

        public static StrikeZoneKind Classify(MapParent parent)
        {
            if (parent == null) return StrikeZoneKind.Ours;
            if (ActiveMaps.IsOurTempSite(parent)) return StrikeZoneKind.Ours;
            if (parent is Settlement settlement)
            {
                if (settlement.Faction != null && settlement.Faction.HostileTo(Faction.OfPlayer))
                    return StrikeZoneKind.EnemySettlement;
                return StrikeZoneKind.NeutralSite;
            }
            if (parent is Site) return StrikeZoneKind.NeutralSite;
            return StrikeZoneKind.NeutralSite;
        }

        private static Map FindMap(int mapId, PlanetTile tile)
        {
            if (mapId >= 0)
            {
                Map byId = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
                if (byId != null) return byId;
            }
            return tile.Valid ? Current.Game.FindMap(tile) : null;
        }

        /// <summary>
        /// EndZone must find the strike map even when called from the railgun home map
        /// or when uniqueID lookup fails — prefer tile, then current map.
        /// </summary>
        private static Map ResolveStrikeMap(int mapId, PlanetTile tile)
        {
            Map map = FindMap(mapId, tile);
            if (map != null) return map;

            if (tile.Valid && Find.Maps != null)
            {
                for (int i = 0; i < Find.Maps.Count; i++)
                {
                    Map m = Find.Maps[i];
                    if (m != null && m.Tile == tile)
                        return m;
                }
            }

            Map cur = Find.CurrentMap;
            if (cur == null) return null;
            if (mapId >= 0 && cur.uniqueID == mapId) return cur;
            if (tile.Valid && cur.Tile == tile) return cur;
            return null;
        }

        private static void ResolveRimatomics()
        {
            if (Resolved) return;
            Resolved = true;
            RailgunType = AccessTools.TypeByName("Rimatomics.Building_Railgun");
            EnergyWeaponType = AccessTools.TypeByName("Rimatomics.Building_EnergyWeapon");
            LongTargetField = EnergyWeaponType != null ? AccessTools.Field(EnergyWeaponType, "longTargetInt") : null;
            ResetForcedTargetMethod = EnergyWeaponType != null
                ? AccessTools.Method(EnergyWeaponType, "ResetForcedTarget")
                : null;
        }

        private static void ClearRailgunTargets(PlanetTile tile)
        {
            ResolveRimatomics();
            if (RailgunType == null || Find.Maps == null) return;
            foreach (Map map in Find.Maps)
            {
                if (map?.listerThings == null) continue;
                List<Thing> things = map.listerThings.AllThings;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t == null || !RailgunType.IsInstanceOfType(t)) continue;
                    if (!TryReadLongTarget(t, out GlobalTargetInfo gti) || !gti.IsValid) continue;
                    if (tile.Valid && !LongTargetMatchesTile(gti, tile)) continue;
                    TryResetRailgun(t);
                }
            }
        }

        private static void ClearAllRailgunWorldTargets()
        {
            // Always ResetForcedTarget on every railgun — do not gate on readable longTarget
            // (map End used to skip reset when the field read failed / already partially cleared).
            ResolveRimatomics();
            InvalidateRailgunTargetCache();
            FireMissionUi.InvalidateStrikeGizmoCache();
            if (RailgunType == null || Find.Maps == null) return;
            foreach (Map map in Find.Maps)
            {
                if (map?.listerThings == null) continue;
                List<Thing> things = map.listerThings.AllThings;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t == null || !RailgunType.IsInstanceOfType(t)) continue;
                    TryResetRailgun(t);
                }
            }
        }

        private static bool MapHasPlayerPawns(Map map) => OutpostRaidCombat.HasLivingPlayerPawn(map);

        private static void UnloadMapKeepWorldObject(Map map)
        {
            // Same foreign-pin guard as Salvage UnloadMap — never hard-Deinit under third-party pins.
            StrikeSalvageUtility.UnloadMap(map);
        }

        /// <summary>
        /// Keep defeated-settlement map in memory so caravan/shuttle Enter works (needs HasMap)
        /// and loot from the strike remains. Pin until player visits and leaves; then vanilla
        /// DestroyedSettlement.ShouldRemoveMapNow removes map + ruins marker.
        /// </summary>
        private static void HoldRuinsMapForLaterEnter(Map map)
        {
            if (map == null) return;
            try
            {
                MapComponent_StrikeLootWindow loot = map.GetComponent<MapComponent_StrikeLootWindow>();
                loot?.AbortKeepWorldObject();
                ActiveMaps.Pin(map, PinTokenLoot, ActiveMapReason.LootWindow);
                MapComponent_RuinsHold hold = map.GetComponent<MapComponent_RuinsHold>();
                if (hold == null)
                {
                    hold = new MapComponent_RuinsHold(map);
                    map.components.Add(hold);
                }
                hold.Arm();
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] HoldRuinsMapForLaterEnter: {e.Message}");
            }
        }

        private static void TryResetRailgun(Thing railgun)
        {
            try
            {
                if (ResetForcedTargetMethod != null)
                {
                    ResetForcedTargetMethod.Invoke(railgun, null);
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] ResetForcedTarget failed: {e.Message}");
            }
            TryClearLongTarget(railgun);
        }

        private static bool TryReadLongTarget(Thing railgun, out GlobalTargetInfo gti)
        {
            gti = GlobalTargetInfo.Invalid;
            if (LongTargetField == null) return false;
            try
            {
                object boxed = LongTargetField.GetValue(railgun);
                if (boxed is GlobalTargetInfo direct)
                {
                    gti = direct;
                    return true;
                }
                if (boxed != null && boxed.GetType() == typeof(GlobalTargetInfo))
                {
                    gti = (GlobalTargetInfo)boxed;
                    return true;
                }
            }
            catch
            {
                // ignore
            }
            return false;
        }

        private static void TryClearLongTarget(Thing railgun)
        {
            if (LongTargetField == null) return;
            try
            {
                LongTargetField.SetValue(railgun, GlobalTargetInfo.Invalid);
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] Clear longTargetInt failed: {e.Message}");
            }
        }

        private static bool LongTargetMatchesTile(GlobalTargetInfo gti, PlanetTile tile)
        {
            if (!tile.Valid || !gti.IsValid) return false;
            try
            {
                if (gti.Tile == tile) return true;
            }
            catch
            {
                // ignore PlanetTile compare quirks
            }
            try
            {
                if (gti.HasWorldObject && gti.WorldObject is MapParent mp && mp.Tile == tile)
                    return true;
            }
            catch
            {
                // ignore
            }
            try
            {
                if (gti.Map != null && gti.Map.Tile == tile)
                    return true;
            }
            catch
            {
                // ignore
            }
            return false;
        }

        public static bool AnyRailgunExists()
        {
            ResolveRimatomics();
            if (RailgunType == null) return false;
            foreach (Map map in Find.Maps)
            {
                foreach (Thing t in map.listerThings.AllThings)
                {
                    if (t != null && RailgunType.IsInstanceOfType(t) && t.Spawned)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Clear railgun forced targets only — zone/map pin stay active.</summary>
        public static void StopFire()
        {
            if (!Active && !AnyRailgunHasWorldTarget()) return;
            try { Find.Targeter?.StopTargeting(); } catch { /* ignore */ }
            ClearAllRailgunWorldTargets();
            Messages.Message("Crystallize_AM_StopFire".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>
        /// Retarget local cell on the active strike map without tearing down the zone.
        /// </summary>
        public static void BeginLocalRetarget()
        {
            if (!Active) return;
            Map map = FindMap(MapUniqueId, Tile);
            if (map == null)
            {
                Messages.Message("Crystallize_AM_MapFailed".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            ResolveRimatomics();
            if (RailgunType == null)
            {
                Messages.Message("Crystallize_AM_MapFailed".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            List<Thing> guns = CollectRailgunsInRange(map.Tile);
            if (guns.Count == 0)
            {
                Messages.Message("Crystallize_AM_NoGunsInRange".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            Current.Game.CurrentMap = map;
            CameraJumper.TryHideWorld();

            TargetingParameters parms = null;
            try
            {
                MethodInfo forMission = AccessTools.Method(RailgunType, "ForFireMission");
                if (forMission != null)
                    parms = forMission.Invoke(null, null) as TargetingParameters;
            }
            catch { /* fallback below */ }

            if (parms == null)
            {
                parms = new TargetingParameters
                {
                    canTargetPawns = true,
                    canTargetBuildings = true,
                    canTargetLocations = true
                };
            }

            MethodInfo fireMission = AccessTools.Method(RailgunType, "FireMission");
            if (fireMission == null) return;

            int tileId = (int)map.Tile;
            int mapId = map.uniqueID;
            Texture2D tex = FireMissionUi.TargetIcon;

            Find.Targeter.BeginTargeting(
                parms,
                delegate(LocalTargetInfo x)
                {
                    for (int i = 0; i < guns.Count; i++)
                    {
                        try
                        {
                            fireMission.Invoke(guns[i], new object[] { tileId, x, mapId });
                        }
                        catch (Exception e)
                        {
                            Log.Warning($"[CrystallizeActiveMaps] FireMission retarget failed: {e.Message}");
                        }
                    }
                },
                (Pawn)null,
                (Action)null,
                tex);
        }

        private static List<Thing> CollectRailgunsInRange(PlanetTile targetTile)
        {
            var result = new List<Thing>();
            ResolveRimatomics();
            if (RailgunType == null || !targetTile.Valid) return result;
            PropertyInfo worldRangeProp = AccessTools.Property(RailgunType, "WorldRange");

            foreach (Map map in Find.Maps)
            {
                List<Thing> things = map.listerThings.AllThings;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t == null || !t.Spawned || !RailgunType.IsInstanceOfType(t)) continue;
                    int range = 0;
                    try
                    {
                        if (worldRangeProp != null)
                            range = (int)worldRangeProp.GetValue(t);
                    }
                    catch { continue; }

                    int dist = Find.WorldGrid.TraversalDistanceBetween(
                        t.Map.Tile, targetTile, passImpassable: true, maxDist: int.MaxValue, false);
                    if (dist > range) continue;
                    result.Add(t);
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Holds a DestroyedSettlement combat map after remote railgun defeat until the player
    /// enters and leaves, or dismisses it via the world-map abandon gizmo.
    /// </summary>
    public class MapComponent_RuinsHold : MapComponent
    {
        private bool armed;
        private bool playerVisited;

        public bool Armed => armed;

        public MapComponent_RuinsHold(Map map) : base(map) { }

        public void Arm()
        {
            armed = true;
        }

        public void Release()
        {
            armed = false;
        }

        public override void MapComponentTick()
        {
            if (!armed || map?.mapPawns == null) return;

            bool hasPlayer = OutpostRaidCombat.HasLivingPlayerPawn(map);

            if (hasPlayer)
            {
                playerVisited = true;
                return;
            }

            if (!playerVisited) return;

            // Player left — drop pin so vanilla DestroyedSettlement cleanup can run.
            Release();
            ActiveMaps.Unpin(map, RailgunStrikeZone.PinTokenLoot);
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref armed, "crystallizeAmRuinsHold", false);
            Scribe_Values.Look(ref playerVisited, "crystallizeAmRuinsVisited", false);
        }
    }

    public class MapComponent_StrikeLootWindow : MapComponent
    {
        private int ticksLeft = -1;
        private bool removeWorldObject;
        private int parentId = -1;
        private bool waitingForPlayerLeave;
        private bool notifiedWaitingForLeave;

        /// <summary>True while countdown runs, or while holding the map until colonists leave.</summary>
        public bool Active => ticksLeft >= 0 || waitingForPlayerLeave;
        public int TicksLeft => waitingForPlayerLeave ? 0 : Math.Max(0, ticksLeft);
        public bool WaitingForPlayerLeave => waitingForPlayerLeave;

        public MapComponent_StrikeLootWindow(Map map) : base(map) { }

        public void Begin(int ticks, bool removeWorldObjectWhenDone)
        {
            ticksLeft = ticks;
            removeWorldObject = removeWorldObjectWhenDone;
            parentId = map?.Parent?.ID ?? -1;
            waitingForPlayerLeave = false;
            notifiedWaitingForLeave = false;
        }

        /// <summary>Cancel loot hold without unloading — caller unloads / keeps world object.</summary>
        public void AbortKeepWorldObject()
        {
            ticksLeft = -1;
            removeWorldObject = false;
            waitingForPlayerLeave = false;
            notifiedWaitingForLeave = false;
        }

        /// <summary>Skip remaining loot hold and unload/destroy when safe.</summary>
        public void ForceFinishNow()
        {
            if (!Active) return;
            ticksLeft = -1;
            TryFinalizeOrWaitForPlayer();
        }

        /// <summary>
        /// Driven from WorldComponent — more reliable than MapComponentTick alone
        /// (some setups stall non-current map components).
        /// </summary>
        public void TickFromWorld()
        {
            if (waitingForPlayerLeave)
            {
                if (!HasPlayerPawnsOnMap(map))
                    FinalizeCleanup();
                return;
            }

            if (ticksLeft < 0) return;
            ticksLeft--;
            if (ticksLeft > 0) return;
            ticksLeft = -1;
            TryFinalizeOrWaitForPlayer();
        }

        public override void MapComponentTick()
        {
            // Intentionally empty: countdown is WorldComponent_FireMissionZone.TickLootWindows.
        }

        private void TryFinalizeOrWaitForPlayer()
        {
            if (HasPlayerPawnsOnMap(map))
            {
                waitingForPlayerLeave = true;
                // Keep pin so vanilla does not yank the map under colonists.
                ActiveMaps.Pin(map, RailgunStrikeZone.PinTokenLoot, ActiveMapReason.LootWindow);
                if (!notifiedWaitingForLeave)
                {
                    notifiedWaitingForLeave = true;
                    Messages.Message(
                        "Crystallize_AM_LootWindowWaitForLeave".Translate(),
                        MessageTypeDefOf.NeutralEvent,
                        historical: false);
                }
                return;
            }

            FinalizeCleanup();
        }

        private static bool HasPlayerPawnsOnMap(Map m)
        {
            if (m?.mapPawns?.AllPawnsSpawned == null) return false;
            try
            {
                IReadOnlyList<Pawn> spawned = m.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < spawned.Count; i++)
                {
                    Pawn p = spawned[i];
                    if (p == null || p.Dead) continue;
                    if (p.Faction != null && p.Faction.IsPlayer)
                        return true;
                }
            }
            catch
            {
                // ignore API variance
            }
            return false;
        }

        private void FinalizeCleanup()
        {
            waitingForPlayerLeave = false;
            notifiedWaitingForLeave = false;

            Map m = map;
            MapParent parent = m?.Parent;
            if (parent == null && parentId >= 0 && Find.WorldObjects != null)
            {
                List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] != null && all[i].ID == parentId && all[i] is MapParent mp)
                    {
                        parent = mp;
                        break;
                    }
                }
            }

            // Safety: never wipe a map that still has player pawns.
            if (HasPlayerPawnsOnMap(m))
            {
                waitingForPlayerLeave = true;
                ActiveMaps.Pin(m, RailgunStrikeZone.PinTokenLoot, ActiveMapReason.LootWindow);
                return;
            }

            // Settlements / defeat ruins stay; everything else (Site camps, our temp markers, …) goes.
            bool destroySite = removeWorldObject
                && parent != null
                && !(parent is Settlement)
                && !(parent is DestroyedSettlement);

            // Hard Deinit is forbidden while any foreign ActiveMaps pin remains
            // (quest / SOS2 / other consumers). UnloadMap drops only OwnedPinTokens then aborts if still pinned.
            if (m != null && Find.Maps != null && Find.Maps.Contains(m))
            {
                if (!StrikeSalvageUtility.UnloadMap(m))
                {
                    Log.Warning(
                        $"[CrystallizeActiveMaps] Loot FinalizeCleanup: map {m.uniqueID} kept — foreign pin(s) " +
                        $"({ActiveMaps.DescribePins(m)}). Skipped DeinitAndRemoveMap / site destroy.");
                    return;
                }
            }

            if (destroySite && parent != null && !parent.Destroyed)
            {
                try
                {
                    parent.Destroy();
                    Messages.Message("Crystallize_AM_LootWindowEnded".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
                }
                catch (Exception e)
                {
                    Log.Warning($"[CrystallizeActiveMaps] Destroy strike/site after loot window: {e.Message}");
                }
            }
            else
            {
                Messages.Message("Crystallize_AM_LootWindowEnded".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
            }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref ticksLeft, "crystallizeAmLootTicks", -1);
            Scribe_Values.Look(ref removeWorldObject, "crystallizeAmLootRemoveSite", false);
            Scribe_Values.Look(ref parentId, "crystallizeAmLootParentId", -1);
            Scribe_Values.Look(ref waitingForPlayerLeave, "crystallizeAmLootWaitLeave", false);
            Scribe_Values.Look(ref notifiedWaitingForLeave, "crystallizeAmLootWaitLeaveNotified", false);
        }
    }

    public class WorldComponent_FireMissionZone : WorldComponent
    {
        private int tickGate;

        public WorldComponent_FireMissionZone(World world) : base(world) { }

        public override void ExposeData()
        {
            base.ExposeData();
            RailgunStrikeZone.ExposeData();
        }

        public override void WorldComponentTick()
        {
            TickLootWindows();

            if (!RailgunStrikeZone.Active) return;
            tickGate++;
            if (tickGate < 60) return;
            tickGate = 0;
            // Backup (once/sec): end site conditions if generators already gone while fire continues.
            RailgunStrikeZone.TryClearConditionsIfGeneratorsGone(notify: true);
            if (!RailgunStrikeZone.AnyRailgunExists())
                RailgunStrikeZone.EndZone(RailgunStrikeEndReason.GunsGone);
        }

        private static void TickLootWindows()
        {
            if (Find.Maps == null || Find.Maps.Count == 0) return;
            // Backwards: FinalizeCleanup may remove the map mid-loop.
            for (int i = Find.Maps.Count - 1; i >= 0; i--)
            {
                Map map = Find.Maps[i];
                if (map == null) continue;
                MapComponent_StrikeLootWindow loot = map.GetComponent<MapComponent_StrikeLootWindow>();
                if (loot == null || !loot.Active) continue;
                try { loot.TickFromWorld(); }
                catch (Exception e) { Log.Warning($"[CrystallizeActiveMaps] Loot tick: {e.Message}"); }
            }
        }
    }
}
