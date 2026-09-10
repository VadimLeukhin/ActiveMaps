using HarmonyLib;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Sticky destroy tracker + QuestParkScope soft-Deinit (UnloadMap only).
	/// </summary>
	[HarmonyPatch(typeof(Thing), nameof(Thing.Destroy))]
	public static class Patch_QuestDestroyTracker_ThingDestroy
	{
		static bool Prefix(Thing __instance)
		{
			if (__instance == null || __instance.Destroyed) return true;

			// Safety net: park instead of Destroy while our UnloadMap Deinits.
			if (QuestParkScope.TryParkInsteadOfDestroy(__instance))
				return false;

			// Mid-GenStep wipes (plants under rock chunks, etc.) — never sticky-track.
			if (MapGenerator.mapBeingGenerated != null)
				return true;

			// Regen cull during Restore must not sticky-record (would wipe restored terminal next enter).
			Map map = __instance.Map;
			if (!QuestDestroyedLedger.SuppressDestroyTracking
			    && map != null
			    && QuestDestroyedLedger.IsSignificant(__instance))
				MapComponent_QuestDestroyTracker.Ensure(map)?.RecordDestroyed(__instance);
			return true;
		}
	}
}
