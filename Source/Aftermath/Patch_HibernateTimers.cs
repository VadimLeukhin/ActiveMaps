using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;
using Verse.AI.Group;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// While hibernating: don't false-fire NoActiveThreats; defer distress ambush to FactQueue.
	/// </summary>
	[HarmonyPatch(typeof(GenHostility), nameof(GenHostility.AnyHostileActiveThreatToPlayer),
		new[] { typeof(Map), typeof(bool), typeof(bool) })]
	public static class Patch_GenHostility_HibernateThreats
	{
		static void Postfix(Map map, ref bool __result)
		{
			if (__result || map == null) return;
			if (MapHibernation.ShouldSkipTicks(map))
				__result = true;
		}
	}

	[HarmonyPatch]
	public static class Patch_DistressCallAmbush_Hibernate
	{
		static bool Prepare()
		{
			return ModsConfig.AnomalyActive
			       && AccessTools.TypeByName("RimWorld.QuestGen.QuestPart_DistressCallAmbush") != null;
		}

		static System.Reflection.MethodBase TargetMethod()
			=> AccessTools.Method(typeof(QuestPart_DistressCallAmbush),
				nameof(QuestPart_DistressCallAmbush.Notify_QuestSignalReceived));

		static bool Prefix(QuestPart_DistressCallAmbush __instance, Signal signal,
			Site ___site, string ___inSignal, float ___points)
		{
			if (___site == null) return true;
			if (signal.tag != ___inSignal) return true;
			Map map = ___site.Map;
			if (map == null || !MapHibernation.ShouldSkipTicks(map)) return true;

			StrikeAftermathEntry e = WorldComponent_StrikeAftermath.Get()?.GetByWorldObject(___site);
			if (e == null)
			{
				e = WorldComponent_StrikeAftermath.Get()?.GetOrCreate(___site, StrikeAftermathMode.PreserveHold);
				e.hibernating = true;
			}

			MapHibernation.EnqueueFact(e, SiteFactKind.RaidDue, Find.TickManager.TicksGame,
				$"DistressAmbush points={___points}",
				points: ___points,
				dedupeKey: "distressAmbush");
			Log.Message(
				$"[CrystallizeActiveMaps] Deferred DistressCall ambush while hibernating " +
				$"({___site.LabelCap}, points={___points}).");
			return false;
		}
	}

	/// <summary>Apply deferred ambush / raid facts on the woken map.</summary>
	public static class SiteFactApplicators
	{
		public static void ApplyRaidDue(Map map, Site site, float points, string note)
		{
			if (map == null) return;
			if (note != null && note.StartsWith("DistressAmbush", StringComparison.Ordinal))
			{
				TrySpawnDistressAmbush(map, site, points);
				return;
			}

			// Generic: force a points-scaled raid on this map if possible.
			try
			{
				IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
				parms.forced = true;
				if (points > 0f) parms.points = Math.Max(parms.points, points);
				IncidentDef raid = IncidentDefOf.RaidEnemy;
				if (raid?.Worker != null && raid.Worker.CanFireNow(parms))
					raid.Worker.TryExecute(parms);
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] RaidDue apply: {e.Message}");
			}
		}

		public static void TrySpawnDistressAmbush(Map map, Site site, float points)
		{
			if (map == null || !ModsConfig.AnomalyActive) return;
			try
			{
				List<Thing> burrows = map.listerThings.ThingsOfDef(ThingDefOf.PitBurrow)
					?.Where(t => t != null && !t.Destroyed).ToList();
				if (burrows == null || burrows.Count == 0)
				{
					Log.Warning("[CrystallizeActiveMaps] DistressAmbush: no PitBurrow on wake — skipped.");
					return;
				}

				var pg = new PawnGroupMakerParms
				{
					groupKind = PawnGroupKindDefOf.Fleshbeasts,
					points = points > 0f ? points : 500f,
					faction = Faction.OfEntities,
					raidStrategy = RaidStrategyDefOf.ImmediateAttack
				};
				List<Pawn> pawns = PawnGroupMakerUtility.GeneratePawns(pg).ToList();
				if (pawns.Count == 0) return;

				LordMaker.MakeNewLord(Faction.OfEntities, new LordJob_FleshbeastAssault(), map, pawns);
				Thing burrow = burrows.RandomElement();
				List<IntVec3> cells = pawns.Select(p => CellFinder.RandomClosewalkCellNear(burrow.Position, map, 8)).ToList();
				var request = new SpawnRequest(pawns.Cast<Thing>().ToList(), cells, 1, 1f);
				map.deferredSpawner.AddRequest(request);
				Find.LetterStack.ReceiveLetter(
					"DistressSignalAmbushLabel".Translate(),
					"DistressSignalAmbushText".Translate(),
					LetterDefOf.ThreatBig,
					burrow);
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] DistressAmbush apply: {e.Message}");
			}
		}
	}
}
