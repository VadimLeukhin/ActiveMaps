using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
    /// <summary>Show remaining loot-hold time on Site inspect (camps, quest sites, …).</summary>
    [HarmonyPatch(typeof(Site), nameof(Site.GetInspectString))]
    public static class Patch_Site_LootInspectString
    {
        public static void Postfix(Site __instance, ref string __result)
        {
            if (__instance == null || !__instance.HasMap) return;
            MapComponent_StrikeLootWindow loot = __instance.Map?.GetComponent<MapComponent_StrikeLootWindow>();
            if (loot == null || !loot.Active) return;

            string line = loot.WaitingForPlayerLeave
                ? "Crystallize_AM_LootWindowWaitForLeaveInspect".Translate()
                : "Crystallize_AM_LootWindow".Translate(GenDate.ToStringTicksToPeriod(loot.TicksLeft));
            if (__result.NullOrEmpty())
                __result = line;
            else
                __result = __result + "\n" + line;
        }
    }
}
