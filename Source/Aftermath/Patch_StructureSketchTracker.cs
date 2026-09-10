using HarmonyLib;
using Verse;

namespace Crystallize.ActiveMaps
{
	[HarmonyPatch(typeof(Thing), nameof(Thing.Destroy))]
	public static class Patch_StructureSketch_ThingDestroy
	{
		static void Prefix(Thing __instance)
		{
			if (!(__instance is Building b) || b.Map == null || b.Destroyed) return;
			if (MapGenerator.mapBeingGenerated != null) return;
			b.Map.GetComponent<MapComponent_VirtualGarrisonStructureTracker>()?.RecordDestroyed(b);
		}
	}
}
