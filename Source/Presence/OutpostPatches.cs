using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	[HarmonyPatch(typeof(CaravanArrivalAction_Enter), nameof(CaravanArrivalAction_Enter.CanEnter))]
	public static class Patch_CaravanEnter_BlockOutpost
	{
		static void Postfix(MapParent mapParent, ref FloatMenuAcceptanceReport __result)
		{
			if (mapParent is AbstractOutpost)
				__result = FloatMenuAcceptanceReport.WithFailMessage("Crystallize_AM_NoPlayerEnter".Translate());
		}
	}

	[HarmonyPatch(typeof(ActiveMaps), nameof(ActiveMaps.EnsureLoadedMap))]
	public static class Patch_EnsureLoadedMap_BlockPlayerOutpost
	{
		static bool Prefix(PlanetTile tile, ref Map __result)
		{
			MapParent mp = Find.WorldObjects?.MapParentAt(tile);
			if (mp is AbstractOutpost ao && !ao.RaidActive && !ao.HasMap)
			{
				Log.Warning("[CrystallizeActiveMaps] EnsureLoadedMap blocked for AbstractOutpost (raid-only).");
				__result = null;
				return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
	public static class Patch_Caravan_FormOutpostGizmo
	{
		static void Postfix(Caravan __instance, ref IEnumerable<Gizmo> __result)
		{
			if (__instance == null || !__instance.IsPlayerControlled) return;
			List<Gizmo> list = __result.ToList();
			list.Add(new Command_Action
			{
				defaultLabel = "Crystallize_AM_FormOutpost".Translate(),
				defaultDesc = "Crystallize_AM_FormOutpostDesc".Translate(),
				action = () =>
				{
					if (TileBlocksOutpostForm(__instance.Tile, ignore: __instance))
					{
						Messages.Message("Crystallize_AM_TileOccupied".Translate(), MessageTypeDefOf.RejectInput, false);
						return;
					}
					AbstractOutpost.CreateFromCaravan(__instance, __instance.Tile);
				}
			});
			__result = list;
		}

		/// <summary>
		/// Caravans always sit on the tile — they must not count as "occupied".
		/// Block on MapParents/sites/other non-caravan world objects.
		/// </summary>
		public static bool TileBlocksOutpostForm(PlanetTile tile, Caravan ignore = null)
		{
			foreach (WorldObject wo in Find.WorldObjects.ObjectsAt(tile))
			{
				if (wo == null || wo == ignore) continue;
				if (wo is Caravan) continue;
				return true;
			}
			return false;
		}
	}
}
