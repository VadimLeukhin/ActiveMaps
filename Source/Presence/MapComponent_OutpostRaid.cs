using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Lives on the outpost raid map: victory detect + map-visible fold gizmo
	/// (WorldObject gizmos are easy to miss while fighting on the local map).
	/// </summary>
	public class MapComponent_OutpostRaid : MapComponent
	{
		public int outpostId = -1;
		bool victoryHandled;

		public MapComponent_OutpostRaid(Map map) : base(map) { }

		public static MapComponent_OutpostRaid Ensure(Map map, AbstractOutpost outpost)
		{
			if (map == null || outpost == null) return null;
			MapComponent_OutpostRaid comp = map.GetComponent<MapComponent_OutpostRaid>();
			if (comp == null)
			{
				comp = new MapComponent_OutpostRaid(map);
				map.components.Add(comp);
			}
			comp.outpostId = outpost.ID;
			comp.victoryHandled = false;
			return comp;
		}

		public AbstractOutpost ResolveOutpost()
		{
			if (outpostId < 0 || Find.WorldObjects == null) return null;
			foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
			{
				if (wo is AbstractOutpost ao && ao.ID == outpostId && !ao.Destroyed)
					return ao;
			}
			return null;
		}

		public override void MapComponentTick()
		{
			if (Find.TickManager.TicksGame % 30 != 0) return;
			AbstractOutpost ao = ResolveOutpost();
			if (ao == null || !ao.RaidActive) return;
			ao.CheckRaidVictoryFromMap(map, ref victoryHandled);
		}

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Values.Look(ref outpostId, "outpostId", -1);
			Scribe_Values.Look(ref victoryHandled, "victoryHandled", false);
		}
	}

	public static class OutpostRaidCombat
	{
		public static bool HasActiveHostileThreat(Map map)
		{
			if (map?.mapPawns?.AllPawnsSpawned == null) return false;
			IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
			for (int i = 0; i < pawns.Count; i++)
			{
				Pawn p = pawns[i];
				if (p == null || p.Dead || p.Destroyed) continue;
				if (!p.HostileTo(Faction.OfPlayer)) continue;
				if (GenHostility.IsActiveThreatToPlayer(p))
					return true;
			}
			return false;
		}

		/// <summary>
		/// Living player-faction pawns still on the map (spawned, not dead).
		/// Do NOT use FreeColonists / AnyColonistSpawned — those are too loose for unload gates
		/// (and must not treat corpses as blockers; cryptosleep colonists still count as living).
		/// </summary>
		public static bool HasLivingPlayerPawn(Map map)
		{
			if (map?.mapPawns?.AllPawnsSpawned == null) return false;
			IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
			for (int i = 0; i < pawns.Count; i++)
			{
				Pawn p = pawns[i];
				if (p == null || p.Destroyed || p.Dead) continue;
				if (!p.Spawned) continue;
				if (p.Faction != Faction.OfPlayer) continue;
				// InnerPawn of a Corpse is not Spawned as a map pawn; Dead covers edge cases.
				return true;
			}
			return false;
		}
	}
}
