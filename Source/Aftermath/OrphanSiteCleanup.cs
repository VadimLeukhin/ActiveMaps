using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// After a quest ends (Fail or Success), destroy Preserve Sites that nothing else still needs.
	/// </summary>
	public static class OrphanSiteCleanup
	{
		static readonly HashSet<int> inFlight = new HashSet<int>();

		public static void OnQuestEnded(Quest quest, QuestEndOutcome outcome)
		{
			if (quest == null) return;
			if (outcome != QuestEndOutcome.Fail && outcome != QuestEndOutcome.Success)
				return;

			var sites = new HashSet<Site>();
			CollectSitesFromQuest(quest, sites);
			foreach (Site site in sites)
			{
				if (!ShouldDestroyOrphan(site, quest)) continue;
				TryDestroyOrphanSite(site, outcome == QuestEndOutcome.Success ? "quest success" : "quest fail");
			}
		}

		static void CollectSitesFromQuest(Quest quest, HashSet<Site> into)
		{
			foreach (QuestPart part in quest.PartsListForReading)
			{
				if (part == null) continue;
				var fields = QuestPartFieldCache.For(part);
				for (int i = 0; i < fields.Length; i++)
				{
					object val;
					try { val = fields[i].GetValue(part); } catch { continue; }
					if (val is Site site && !site.Destroyed)
						into.Add(site);
				}
			}
		}

		static bool ShouldDestroyOrphan(Site site, Quest endedQuest)
		{
			if (site == null || site.Destroyed) return false;
			if (site.Faction != null && site.Faction.IsPlayer) return false;

			StrikeMapClass cls = StrikeMapClassifier.Classify(site);
			if (cls != StrikeMapClass.PreserveSite && cls != StrikeMapClass.EphemeralEmpty)
				return false;

			if (Find.QuestManager != null)
			{
				List<Quest> quests = Find.QuestManager.QuestsListForReading;
				for (int i = 0; i < quests.Count; i++)
				{
					Quest q = quests[i];
					if (q == null || q == endedQuest) continue;
					if (q.State != QuestState.Ongoing) continue;
					if (QuestSignificantSetTouches(q, site)) return false;
				}
			}

			if (site.HasMap && FloorEntranceUtility.HasLivingPlayerOnMapOrLinkedFloors(site.Map))
				return false;

			return true;
		}

		static bool QuestSignificantSetTouches(Quest q, Site site)
		{
			foreach (QuestPart part in q.PartsListForReading)
			{
				if (part == null) continue;
				var fields = QuestPartFieldCache.For(part);
				for (int i = 0; i < fields.Length; i++)
				{
					object val;
					try { val = fields[i].GetValue(part); } catch { continue; }
					if (ReferenceEquals(val, site)) return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Strike End: hostage already gone — fail owning quests and remove the Site.
		/// Terminals: Ideo owns Fail (Destroyed, even post-Hacked) / Success (Hacked+MapRemoved).
		/// </summary>
		public static void OnCriticalObjectiveDestroyed(Site site)
		{
			if (site == null || site.Destroyed) return;
			if (site.HasMap && FloorEntranceUtility.HasLivingPlayerOnMapOrLinkedFloors(site.Map))
				return;

			FailOngoingQuestsTouching(site);
			TryDestroyOrphanSite(site, "objective destroyed");
		}

		static void FailOngoingQuestsTouching(Site site)
		{
			if (Find.QuestManager == null || site == null) return;
			List<Quest> quests = Find.QuestManager.QuestsListForReading;
			var toFail = new List<Quest>();
			for (int i = 0; i < quests.Count; i++)
			{
				Quest q = quests[i];
				if (q == null || q.State != QuestState.Ongoing) continue;
				if (QuestSignificantSetTouches(q, site))
					toFail.Add(q);
			}
			for (int i = 0; i < toFail.Count; i++)
			{
				try
				{
					toFail[i].End(QuestEndOutcome.Fail, sendLetter: true, playSound: true);
				}
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] Fail quest after objective lost: {e.Message}");
				}
			}
		}

		public static void TryDestroyOrphanSite(Site site, string reason)
		{
			if (site == null || site.Destroyed) return;
			WorldComponent_StrikeAftermath.Get()?.ScheduleOrphanSiteDestroy(site, reason, delayTicks: 120);
		}

		/// <summary>Called from WorldComponent tick after delay — safe vs mid-shell Unload.</summary>
		public static void DestroyOrphanSiteNow(Site site, string reason)
		{
			if (site == null || site.Destroyed) return;
			if (!inFlight.Add(site.ID)) return;

			string label = site.LabelCap;
			int id = site.ID;
			try
			{
				Map map = site.Map;
				if (map != null)
				{
					ActiveMaps.UnpinOwned(map);
					StrikeSalvageUtility.UnloadMap(map);
				}

				var wc = WorldComponent_StrikeAftermath.Get();
				StrikeAftermathEntry e = wc?.GetByWorldObject(site);
				if (e != null)
					wc.Remove(e);

				if (!site.Destroyed)
					site.Destroy();

				Log.Message($"[CrystallizeActiveMaps] Preserve Site destroyed ({reason}): {label} (ID={id}).");
			}
			catch (Exception ex)
			{
				Log.Warning($"[CrystallizeActiveMaps] OrphanSiteCleanup: {ex.Message}");
			}
			finally
			{
				inFlight.Remove(id);
			}
		}
	}

	[HarmonyPatch(typeof(Quest), nameof(Quest.End))]
	public static class Patch_Quest_End_OrphanSiteCleanup
	{
		static void Postfix(Quest __instance, QuestEndOutcome outcome)
		{
			try
			{
				OrphanSiteCleanup.OnQuestEnded(__instance, outcome);
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] Quest.End orphan cleanup: {e.Message}");
			}
		}
	}
}
