using System;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Thin safety net: only while our UnloadMap runs Deinit.
	/// Quest-significant Things that Collect missed → Park instead of Destroy.
	/// </summary>
	public static class QuestParkScope
	{
		static int depth;
		static Map activeMap;
		static ThingOwner parkOwner;
		static int parkedCount;

		public static bool IsActive => depth > 0 && activeMap != null;

		public static IDisposable Enter(Map map)
		{
			depth++;
			if (depth == 1)
			{
				activeMap = map;
				parkedCount = 0;
				parkOwner = ResolveParkOwner(map);
			}
			return new Popper();
		}

		static ThingOwner ResolveParkOwner(Map map)
		{
			MapParent parent = map?.Parent;
			if (parent == null) return null;
			var wc = WorldComponent_StrikeAftermath.Get();
			if (wc == null) return null;
			StrikeAftermathEntry e = wc.GetByWorldObject(parent) ?? wc.GetOrCreate(parent, StrikeAftermathMode.None);
			return e?.loot;
		}

		/// <summary>
		/// If in scope and thing is quest-significant on the unloading map — park and skip Destroy.
		/// </summary>
		public static bool TryParkInsteadOfDestroy(Thing t)
		{
			if (!IsActive || t == null || t.Destroyed) return false;
			if (t.Map != activeMap) return false;
			if (!QuestDestroyedLedger.IsSignificant(t) && t.TryGetComp<CompHackable>() == null)
				return false;
			if (t.Faction == Faction.OfPlayer) return false;

			try
			{
				if (t is Pawn p)
				{
					QuestSignificantSet.ScrubPawnBeforeWorldEvacuate(p);
					p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ExitedMap);
					if (p.Spawned)
						p.DeSpawn(DestroyMode.Vanish);
					Site site = activeMap?.Parent as Site;
					if (!QuestSignificantSet.TryStashQuestPawnOnSite(site, p)
					    && !p.Destroyed && !p.IsWorldPawn())
						Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.KeepForever);
					parkedCount++;
					return true;
				}

				if (parkOwner == null) return false;
				if (t.Spawned)
					t.DeSpawn(DestroyMode.Vanish);
				if (!t.Destroyed)
					parkOwner.TryAddOrTransfer(t, canMergeWithExistingStacks: true);
				parkedCount++;
				return true;
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] QuestParkScope park: {e.Message}");
				return false;
			}
		}

		static void Exit()
		{
			if (depth <= 0) return;
			depth--;
			if (depth == 0)
			{
				if (parkedCount > 0)
				{
					Log.Warning(
						$"[CrystallizeActiveMaps] QuestParkScope: parked {parkedCount} quest thing(s) " +
						$"during UnloadMap (Collect gap). Map was {activeMap?.uniqueID}.");
				}
				activeMap = null;
				parkOwner = null;
				parkedCount = 0;
			}
		}

		struct Popper : IDisposable
		{
			public void Dispose() => Exit();
		}
	}
}
