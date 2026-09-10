using HarmonyLib;
using Verse;

namespace Crystallize.ActiveMaps
{
	[HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
	public static class Patch_Map_MapPreTick_Hibernate
	{
		static bool Prefix(Map __instance)
		{
			MapHibernation.NotifyMapPreTick(__instance);
			return !MapHibernation.ShouldSkipTicks(__instance);
		}
	}

	[HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
	public static class Patch_Map_MapPostTick_Hibernate
	{
		static bool Prefix(Map __instance)
			=> !MapHibernation.ShouldSkipTicks(__instance);
	}

	/// <summary>
	/// TickLists are global — skipping MapPre/PostTick alone still runs Thing.DoTick.
	/// Fast path: no hibernating entries → zero cost beyond one list check inside ShouldSkip.
	/// </summary>
	[HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
	public static class Patch_Thing_DoTick_Hibernate
	{
		static bool Prefix(Thing __instance)
		{
			if (__instance == null || !__instance.Spawned) return true;
			Map map = __instance.Map;
			// Spawned but Map==null: mid-despawn / hibernate edge — skip tick (was: allow → Door NRE).
			if (map == null) return false;
			return !MapHibernation.ShouldSkipTicks(map);
		}
	}
}
