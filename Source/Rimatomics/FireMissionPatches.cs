using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Crystallize.ActiveMaps
{
    /// <summary>
    /// Rimatomics Building_Railgun cctor loads textures — patch only after startup.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class FireMissionHarmonyInit
    {
        static FireMissionHarmonyInit()
        {
            try
            {
                Type railgun = AccessTools.TypeByName("Rimatomics.Building_Railgun");
                if (railgun == null)
                {
                    Log.Message("[CrystallizeActiveMaps] Rimatomics not loaded — railgun bridge skipped.");
                    return;
                }

                var harmony = new Harmony("crystallize.activemaps.rimatomics");

                MethodBase chose = AccessTools.Method(railgun, "ChoseWorldTarget");
                if (chose != null)
                {
                    harmony.Patch(chose,
                        prefix: new HarmonyMethod(typeof(FireMissionRimatomicsPatches),
                            nameof(FireMissionRimatomicsPatches.ChoseWorldTarget_Prefix)));
                }

                MethodBase gizmos = AccessTools.Method(railgun, "GetGizmos");
                if (gizmos != null)
                {
                    harmony.Patch(gizmos,
                        postfix: new HarmonyMethod(typeof(FireMissionRimatomicsPatches),
                            nameof(FireMissionRimatomicsPatches.GetGizmos_Postfix)));
                }

                MethodBase fireMission = AccessTools.Method(railgun, "FireMission");
                if (fireMission != null)
                {
                    harmony.Patch(fireMission,
                        postfix: new HarmonyMethod(typeof(FireMissionRimatomicsPatches),
                            nameof(FireMissionRimatomicsPatches.FireMission_Postfix)));
                }

                Type sabot = AccessTools.TypeByName("Rimatomics.WorldObject_Sabot");
                MethodBase arrived = sabot != null ? AccessTools.Method(sabot, "Arrived") : null;
                if (arrived != null)
                {
                    harmony.Patch(arrived,
                        prefix: new HarmonyMethod(typeof(FireMissionRimatomicsPatches),
                            nameof(FireMissionRimatomicsPatches.SabotArrived_Prefix)));
                }

                Log.Message("[CrystallizeActiveMaps] Rimatomics Fire Mission bridge applied.");
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] Railgun Harmony init failed: {e}");
            }
        }
    }

    public static class FireMissionRimatomicsPatches
    {
        public static void FireMission_Postfix(int tile, LocalTargetInfo targ, int map)
        {
            try
            {
                if (!targ.IsValid) return;
                RailgunStrikeZone.NotifyShotFiredAtTile((PlanetTile)tile);
            }
            catch
            {
                // ignore
            }
        }

        public static bool ChoseWorldTarget_Prefix(Thing __instance, GlobalTargetInfo target, ref bool __result)
        {
            try
            {
                if (!target.IsValid || __instance?.Map == null)
                    return true;

                PropertyInfo worldRangeProp = AccessTools.Property(__instance.GetType(), "WorldRange");
                if (worldRangeProp != null)
                {
                    int worldRange = (int)worldRangeProp.GetValue(__instance);
                    int dist = Find.WorldGrid.TraversalDistanceBetween(
                        __instance.Map.Tile, target.Tile, passImpassable: true, maxDist: int.MaxValue, false);
                    if (dist > worldRange)
                        return true;
                }

                if (target.WorldObject is MapParent ownParent && ownParent.HasMap && ownParent.Map == __instance.Map)
                    return true;

                PlanetTile tile = target.Tile;
                if (RailgunStrikeZone.Active && RailgunStrikeZone.Tile.Valid && RailgunStrikeZone.Tile != tile)
                    RailgunStrikeZone.EndZone(RailgunStrikeEndReason.Retarget);

                if (!RailgunStrikeZone.EnsureMapReady(tile, out string error))
                {
                    Messages.Message(
                        error ?? "Crystallize_AM_MapFailed".Translate(),
                        MessageTypeDefOf.RejectInput,
                        historical: false);
                    __result = false;
                    return false;
                }

                Map map = Current.Game.FindMap(tile);
                if (map != null)
                    ActiveMaps.JumpToMap(map);

                return true;
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] ChoseWorldTarget prefix failed: {e.Message}");
                return true;
            }
        }

        public static IEnumerable<Gizmo> GetGizmos_Postfix(IEnumerable<Gizmo> __result)
        {
            foreach (Gizmo g in __result)
                yield return g;

            if (!RailgunStrikeZone.NeedsEndControl())
                yield break;

            if (RailgunStrikeZone.Active)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Crystallize_AM_ChangeTarget".Translate(),
                    defaultDesc = "Crystallize_AM_ChangeTargetDesc".Translate(),
                    icon = FireMissionUi.TargetIcon,
                    action = RailgunStrikeZone.BeginLocalRetarget
                };
                yield return new Command_Action
                {
                    defaultLabel = "Crystallize_AM_StopFire".Translate(),
                    defaultDesc = "Crystallize_AM_StopFireDesc".Translate(),
                    icon = FireMissionUi.StopFireIcon,
                    action = RailgunStrikeZone.StopFire
                };
            }

            yield return new Command_Action
            {
                defaultLabel = "Crystallize_AM_EndStrike".Translate(),
                defaultDesc = "Crystallize_AM_EndStrikeDesc".Translate(),
                icon = TexCommand.ClearPrioritizedWork,
                action = () => RailgunStrikeZone.EndZone(RailgunStrikeEndReason.Manual)
            };
        }

        /// <summary>If the target map unloaded mid-flight, regenerate so the sabot can land.</summary>
        public static void SabotArrived_Prefix(WorldObject __instance)
        {
            try
            {
                PlanetTile tile = __instance.Tile;
                if (!tile.Valid) return;
                if (Current.Game.FindMap(tile) != null) return;
                if (!RailgunStrikeZone.Active || RailgunStrikeZone.Tile != tile) return;
                RailgunStrikeZone.EnsureMapReady(tile, out _);
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] SabotArrived ensure failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Destroy))]
    public static class Patch_StrikeConditionCauserDestroyed
    {
        public static void Prefix(Thing __instance, ref bool __state)
        {
            __state = false;
            try
            {
                __state = RailgunStrikeZone.IsRelevantConditionCauser(__instance);
            }
            catch
            {
                __state = false;
            }
        }

        public static void Postfix(bool __state)
        {
            if (!__state) return;
            try
            {
                RailgunStrikeZone.TryClearConditionsIfGeneratorsGone(notify: true);
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] Mid-strike condition clear failed: {e.Message}");
            }
        }
    }
}
