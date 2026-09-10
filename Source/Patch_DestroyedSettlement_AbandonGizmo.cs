using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Crystallize.ActiveMaps
{
    /// <summary>
    /// World-map gizmo on DestroyedSettlement: abandon/unload the held combat map
    /// (label matches Ancient Urban Ruins "abandon map" / «Покинуть локацию»).
    /// </summary>
    [HarmonyPatch(typeof(DestroyedSettlement), nameof(DestroyedSettlement.GetGizmos))]
    public static class Patch_DestroyedSettlement_AbandonGizmo
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, DestroyedSettlement __instance)
        {
            foreach (Gizmo g in __result)
                yield return g;

            if (__instance == null || !__instance.HasMap)
                yield break;

            Map map = __instance.Map;
            if (map == null)
                yield break;

            // Only for maps we are holding after a remote railgun defeat (or any pin/loot hold).
            MapComponent_RuinsHold hold = map.GetComponent<MapComponent_RuinsHold>();
            bool ourHold = hold != null && hold.Armed;
            bool pinned = ActiveMaps.IsPinned(map);
            if (!ourHold && !pinned)
                yield break;

            bool hasColonists = OutpostRaidCombat.HasLivingPlayerPawn(map);

            yield return new Command_Action
            {
                defaultLabel = "Crystallize_AM_AbandonRuinsMap".Translate(),
                defaultDesc = "Crystallize_AM_AbandonRuinsMapDesc".Translate(),
                icon = TexCommand.ClearPrioritizedWork,
                Disabled = hasColonists,
                disabledReason = hasColonists
                    ? "Crystallize_AM_AbandonRuinsMapHasColonists".Translate()
                    : null,
                action = () =>
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "Crystallize_AM_ConfirmAbandonRuinsMap".Translate(),
                        () => AbandonRuinsMap(__instance),
                        destructive: true));
                }
            };
        }

        private static void AbandonRuinsMap(DestroyedSettlement ds)
        {
            if (ds == null || ds.Destroyed) return;
            Map map = ds.Map;
            try
            {
                if (map != null)
                {
                    // Refuses Deinit if foreign pins remain — do not Destroy WO while map still held.
                    if (!StrikeSalvageUtility.UnloadMap(map))
                        return;
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] AbandonRuinsMap unload: {e.Message}");
                return;
            }

            if (!ds.Destroyed)
            {
                try { ds.Destroy(); }
                catch (Exception e) { Log.Warning($"[CrystallizeActiveMaps] AbandonRuinsMap destroy: {e.Message}"); }
            }
        }
    }
}
