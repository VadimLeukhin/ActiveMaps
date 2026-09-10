using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>Snapshot world/quest timers onto FactQueue when a map enters hibernation (C2).</summary>
	public static class SiteTimerMirror
	{
		public static void CaptureOnHibernate(Site site, StrikeAftermathEntry entry)
		{
			if (site == null || entry == null) return;
			int now = Find.TickManager?.TicksGame ?? 0;

			if (site.HasWorldObjectTimeout)
			{
				int left = site.WorldObjectTimeoutTicksLeft;
				if (left > 0)
				{
					MapHibernation.EnqueueFact(entry, SiteFactKind.Custom, now + left,
						"WorldObjectTimeout", dedupeKey: "timeout");
				}
			}

			if (site.parts != null)
			{
				for (int i = 0; i < site.parts.Count; i++)
				{
					SitePart part = site.parts[i];
					if (part == null) continue;
					// RaidSource uses lastRaidTick + MTB on Site.Tick (world). Record for inspect/debug.
					if (part.lastRaidTick > 0)
					{
						MapHibernation.EnqueueFact(entry, SiteFactKind.Custom, -1,
							$"lastRaidTick={part.lastRaidTick}", dedupeKey: $"raidSrc:{part.def?.defName}");
					}
				}
			}

			MirrorQuestDelays(site, entry, now);
		}

		static void MirrorQuestDelays(Site site, StrikeAftermathEntry entry, int now)
		{
			if (Find.QuestManager == null) return;
			List<Quest> quests = Find.QuestManager.QuestsListForReading;
			for (int i = 0; i < quests.Count; i++)
			{
				Quest q = quests[i];
				if (q == null || q.State != QuestState.Ongoing) continue;
				if (!QuestTouchesSite(q, site)) continue;
				foreach (QuestPart part in q.PartsListForReading)
				{
					if (part is QuestPart_Delay delay && delay.TicksLeft > 0)
					{
						string key = $"delay:{q.id}:{delay.GetType().Name}:{delay.TicksLeft}";
						MapHibernation.EnqueueFact(entry, SiteFactKind.Custom, now + delay.TicksLeft,
							$"QuestDelay q={q.id} left={delay.TicksLeft}", dedupeKey: key);
					}
				}
			}
		}

		static bool QuestTouchesSite(Quest quest, Site site)
		{
			if (quest == null || site == null) return false;
			foreach (QuestPart part in quest.PartsListForReading)
			{
				if (part == null) continue;
				var fields = QuestPartFieldCache.For(part);
				for (int i = 0; i < fields.Length; i++)
				{
					object val;
					try { val = fields[i].GetValue(part); } catch { continue; }
					if (ReferenceEquals(val, site)) return true;
					if (val is WorldObject wo && wo.ID == site.ID) return true;
				}
			}
			return false;
		}
	}
}
