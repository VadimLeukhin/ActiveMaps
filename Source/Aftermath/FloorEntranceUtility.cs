using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Multi-floor / linked-map entrances (vanilla MapPortal, Fortified lift shafts,
	/// Ancient Urban Ruins stairs, etc.). Detection is by portal building — not mod package.
	/// </summary>
	public static class FloorEntranceUtility
	{
		/// <summary>
		/// Building that opens another floor/pocket map from this map.
		/// Excludes <see cref="PocketMapExit"/> (return hatch on the child map).
		/// </summary>
		public static bool IsFloorEntrance(Thing t)
		{
			if (t == null || t.Destroyed) return false;
			if (t is PocketMapExit) return false;
			if (t is MapPortal) return true;

			MapPortalProperties portal = t.def?.portal;
			if (portal == null) return false;
			// Entrance defs declare a generator; pure exit shells may omit it.
			return portal.pocketMapGenerator != null;
		}

		public static bool MapHasFloorEntrance(Map map)
		{
			if (map?.listerThings == null) return false;
			List<Thing> buildings = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
			if (buildings == null) return false;
			for (int i = 0; i < buildings.Count; i++)
			{
				if (IsFloorEntrance(buildings[i]))
					return true;
			}
			return false;
		}

		/// <summary>Already-generated child pocket maps hanging off this surface map.</summary>
		public static bool MapHasLinkedPocketMaps(Map map)
		{
			if (map == null) return false;
			foreach (Map _ in map.ChildPocketMaps)
				return true;
			return false;
		}

		/// <summary>
		/// Player on surface or any linked pocket — End must not Deinit (pocket destroy would wipe them).
		/// </summary>
		public static bool HasLivingPlayerOnMapOrLinkedFloors(Map map)
		{
			if (OutpostRaidCombat.HasLivingPlayerPawn(map))
				return true;
			if (map == null) return false;
			foreach (Map pocket in map.ChildPocketMaps)
			{
				if (OutpostRaidCombat.HasLivingPlayerPawn(pocket))
					return true;
			}
			return false;
		}
	}
}
