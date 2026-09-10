using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Apply VG / evacuate-restore on every GetOrGenerateMap overload (caravan enter often uses the short one).
	/// </summary>
	[HarmonyPatch]
	public static class Patch_GetOrGenerateMap_VirtualGarrison
	{
		static IEnumerable<MethodBase> TargetMethods()
		{
			foreach (MethodInfo mi in AccessTools.GetDeclaredMethods(typeof(GetOrGenerateMapUtility)))
			{
				if (mi == null || mi.Name != nameof(GetOrGenerateMapUtility.GetOrGenerateMap))
					continue;
				if (mi.ReturnType != typeof(Map))
					continue;
				yield return mi;
			}
		}

		static void Postfix(Map __result)
		{
			if (__result == null) return;
			try
			{
				if (__result.Parent is MapParent mp && mp.Faction != null && !mp.Faction.IsPlayer)
					MapComponent_VirtualGarrisonStructureTracker.Ensure(__result, mp.Faction);
				if (__result.Parent is Site)
					MapComponent_QuestDestroyTracker.Ensure(__result);
				VirtualGarrisonUtility.ApplyOnMapGenerated(__result);
			}
			catch (Exception e) { Log.Warning($"[CrystallizeActiveMaps] VG postfix: {e.Message}"); }
		}
	}

	[HarmonyPatch(typeof(CaravanArrivalAction_Enter), nameof(CaravanArrivalAction_Enter.CanEnter))]
	public static class Patch_CanEnter_SalvageBlock
	{
		static void Postfix(MapParent mapParent, ref FloatMenuAcceptanceReport __result)
		{
			if (mapParent == null) return;
			StrikeAftermathEntry e = WorldComponent_StrikeAftermath.Get()?.GetByWorldObject(mapParent);
			if (e == null || !e.IsSalvageActive) return;
			__result = FloatMenuAcceptanceReport.WithFailMessage("Crystallize_AM_SalvageNoEnter".Translate());
		}
	}

	[HarmonyPatch(typeof(MapParent), nameof(MapParent.GetFloatMenuOptions))]
	public static class Patch_MapParent_SalvageFloatMenu
	{
		public static IEnumerable<FloatMenuOption> Postfix(IEnumerable<FloatMenuOption> __result, MapParent __instance, Caravan caravan)
		{
			foreach (FloatMenuOption opt in __result)
				yield return opt;

			if (caravan == null || __instance == null) yield break;
			StrikeAftermathEntry e = WorldComponent_StrikeAftermath.Get()?.GetByWorldObject(__instance);
			if (e == null || !e.IsSalvageActive) yield break;

			PlanetTile siteTile = StrikeSalvageUtility.SalvageSiteTile(__instance, e);
			string label = "Crystallize_AM_SalvageTakeLoot".Translate(__instance.LabelCap);
			if (StrikeSalvageUtility.CaravanCanTakeSalvageLoot(caravan, siteTile))
			{
				yield return new FloatMenuOption(
					label,
					() => Find.WindowStack.Add(new Dialog_StrikeSalvageTransfer(caravan, e, siteTile)));
			}
			else
			{
				yield return new FloatMenuOption(
					label + ": " + "Crystallize_AM_SalvageMustVisit".Translate(),
					null);
			}
		}
	}

	[HarmonyPatch(typeof(MapParent), nameof(MapParent.GetInspectString))]
	public static class Patch_MapParent_SalvageInspect
	{
		static void Postfix(MapParent __instance, ref string __result)
		{
			if (__instance == null) return;
			string extra = WorldComponent_StrikeAftermath.Get()?.SalvageInspectString(__instance);
			if (extra.NullOrEmpty()) return;
			if (__result.NullOrEmpty()) __result = extra;
			else __result = __result + "\n" + extra;
		}
	}
}
