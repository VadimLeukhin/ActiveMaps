using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Blocks SettlementDefeatUtility.CheckDefeated while VG folds defenders / unloads
	/// (otherwise empty map → instant DestroyedSettlement) and while our strike pin holds the map.
	/// </summary>
	public static class SettlementDefeatSuppress
	{
		[ThreadStatic]
		static int depth;

		public static bool IsActive => depth > 0;

		public static IDisposable Scope() => new ScopeToken();

		sealed class ScopeToken : IDisposable
		{
			bool disposed;
			public ScopeToken() => depth++;
			public void Dispose()
			{
				if (disposed) return;
				disposed = true;
				depth--;
			}
		}

		public static bool ShouldSuppress(Settlement factionBase)
		{
			if (IsActive) return true;
			Map map = factionBase?.Map;
			if (map != null && ActiveMaps.IsPinned(map)) return true;
			return false;
		}
	}

	/// <summary>
	/// Vanilla CheckDefeated ends with FreeColonists.RandomElement() → CaravanAssaultSuccessful.
	/// Remote strike wipe has no colonists → "Getting random element from empty collection" + NRE.
	/// When empty, run the same defeat conversion without the tale.
	/// Also: skip entirely while VG capture / strike pin holds the map (false defeat).
	/// </summary>
	[HarmonyPatch(typeof(SettlementDefeatUtility), nameof(SettlementDefeatUtility.CheckDefeated))]
	public static class Patch_SettlementDefeat_NoColonists
	{
		[ThreadStatic]
		private static bool suppressEmptyColonistTale;

		public static bool Prefix(Settlement factionBase)
		{
			suppressEmptyColonistTale = false;
			try
			{
				if (SettlementDefeatSuppress.ShouldSuppress(factionBase))
					return false;

				Map map = factionBase?.Map;
				if (map?.mapPawns == null) return true;
				if (map.mapPawns.FreeColonists.Count > 0) return true;

				suppressEmptyColonistTale = true;
				CheckDefeatedWithoutAssaultTale(factionBase);
				return false;
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] CheckDefeated prefix: {e.Message}");
				suppressEmptyColonistTale = false;
				return true;
			}
		}

		public static Exception Finalizer(Exception __exception)
		{
			bool skip = suppressEmptyColonistTale;
			suppressEmptyColonistTale = false;
			if (__exception == null) return null;
			if (skip
			    && (__exception is NullReferenceException
			        || __exception is InvalidOperationException
			        || __exception is TargetInvocationException))
			{
				Log.Message("[CrystallizeActiveMaps] Settlement defeated without local colonists — skipped assault tale.");
				return null;
			}
			return __exception;
		}

		public static bool ShouldSkipAssaultTale() => suppressEmptyColonistTale;

		/// <summary>Vanilla CheckDefeated body minus FreeColonists.RandomElement tale.</summary>
		public static void CheckDefeatedWithoutAssaultTale(Settlement factionBase)
		{
			if (factionBase == null || factionBase.Faction == Faction.OfPlayer)
				return;
			if (SettlementDefeatSuppress.ShouldSuppress(factionBase))
				return;

			Map map = factionBase.Map;
			if (map == null || !StrikeMapClassifier.IsHabitationDefeated(map, factionBase))
				return;

			IdeoUtility.Notify_PlayerRaidedSomeone(map.mapPawns.FreeColonistsSpawned);

			WorldObjectDef ruinsDef = factionBase.Tile.LayerDef?.DestroyedSettlementWorldObjectDef;
			if (ruinsDef == null)
			{
				Log.Warning("[CrystallizeActiveMaps] No DestroyedSettlementWorldObjectDef for layer — skipping defeat convert.");
				return;
			}

			DestroyedSettlement destroyedSettlement = (DestroyedSettlement)WorldObjectMaker.MakeWorldObject(ruinsDef);
			destroyedSettlement.Tile = factionBase.Tile;
			destroyedSettlement.SetFaction(factionBase.Faction);
			Find.WorldObjects.Add(destroyedSettlement);

			StringBuilder stringBuilder = new StringBuilder();
			bool hasOtherBase = HasAnyOtherBase(factionBase);
			if (hasOtherBase && destroyedSettlement.TryGetComponent(out TimedDetectionRaids comp))
			{
				TimedDetectionRaids from = factionBase.GetComponent<TimedDetectionRaids>();
				if (from != null)
					comp.CopyFrom(from);
				comp.SetNotifiedSilently();
				if (!string.IsNullOrEmpty(comp.DetectionCountdownTimeLeftString))
					stringBuilder.Append("LetterFactionBaseDefeated".Translate(factionBase.Label, comp.DetectionCountdownTimeLeftString).Resolve());
				else
					stringBuilder.Append("LetterFactionBaseDefeatedNoRaids".Translate(factionBase.Label));
			}
			else
			{
				stringBuilder.Append("LetterFactionBaseDefeatedNoRaids".Translate(factionBase.Label));
			}

			if (!hasOtherBase)
			{
				factionBase.Faction.defeated = true;
				stringBuilder.AppendLine();
				stringBuilder.AppendLine();
				stringBuilder.Append("LetterFactionBaseDefeated_FactionDestroyed".Translate(factionBase.Faction.Name));
			}

			foreach (Faction allFaction in Find.FactionManager.AllFactions)
			{
				if (allFaction.Hidden || allFaction.IsPlayer || allFaction == factionBase.Faction)
					continue;
				if (!allFaction.HostileTo(factionBase.Faction))
					continue;

				FactionRelationKind playerRelationKind = allFaction.PlayerRelationKind;
				Faction.OfPlayer.TryAffectGoodwillWith(allFaction, 20, canSendMessage: false, canSendHostilityLetter: false, HistoryEventDefOf.DestroyedEnemyBase, null);
				stringBuilder.AppendLine();
				stringBuilder.AppendLine();
				stringBuilder.Append("RelationsWith".Translate(allFaction.Name) + ": " + 20.ToStringWithSign());
				allFaction.TryAppendRelationKindChangedInfo(stringBuilder, playerRelationKind, allFaction.PlayerRelationKind);
			}

			Find.LetterStack.ReceiveLetter(
				"LetterLabelFactionBaseDefeated".Translate(),
				stringBuilder.ToString(),
				LetterDefOf.PositiveEvent,
				new GlobalTargetInfo(factionBase.Tile),
				factionBase.Faction);

			map.info.parent = destroyedSettlement;
			factionBase.Destroy();
			Log.Message("[CrystallizeActiveMaps] Settlement defeated without local colonists — skipped assault tale.");
		}

		static bool HasAnyOtherBase(Settlement defeated)
		{
			List<Settlement> settlements = Find.WorldObjects.Settlements;
			for (int i = 0; i < settlements.Count; i++)
			{
				Settlement s = settlements[i];
				if (s.Faction == defeated.Faction && s != defeated)
					return true;
			}
			return false;
		}
	}

	[HarmonyPatch(typeof(TaleRecorder), nameof(TaleRecorder.RecordTale))]
	public static class Patch_TaleRecorder_SkipNullAssault
	{
		public static bool Prefix(TaleDef def, params object[] args)
		{
			if (def == TaleDefOf.CaravanAssaultSuccessful)
			{
				if (Patch_SettlementDefeat_NoColonists.ShouldSkipAssaultTale())
					return false;
				if (args == null || args.Length == 0 || args[0] == null)
					return false;
			}
			return true;
		}
	}
}
