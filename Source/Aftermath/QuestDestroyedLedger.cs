using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>One quest-significant Thing/Pawn destroyed during a strike (sticky across regen).</summary>
	public class QuestDestroyedRecord : IExposable
	{
		public string defName;
		public bool wasPawn;
		public bool wasHackable;
		public List<string> questTags = new List<string>();

		public void ExposeData()
		{
			Scribe_Values.Look(ref defName, "def");
			Scribe_Values.Look(ref wasPawn, "pawn");
			Scribe_Values.Look(ref wasHackable, "hack");
			Scribe_Collections.Look(ref questTags, "tags", LookMode.Value);
			if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				if (questTags == null)
					questTags = new List<string>();
			}
		}
	}

	/// <summary>
	/// Sticky across regen: destroyed terminal/hostage must not respawn on re-enter.
	/// Ideo Hack Fail/Success stays with vanilla quest signals (Destroyed / Hacked+MapRemoved).
	/// Strike End only force-fails for hostages (pawns) — not CompHackable.
	/// </summary>
	public static class QuestDestroyedLedger
	{
		/// <summary>While Restore culls regen duplicates — do not RecordDestroyed (would sticky-cull the real terminal next enter).</summary>
		public static bool SuppressDestroyTracking;

		public static void CaptureFromMap(Map map, StrikeAftermathEntry entry)
		{
			if (map == null || entry == null) return;
			MapComponent_QuestDestroyTracker tracker = map.GetComponent<MapComponent_QuestDestroyTracker>();
			if (tracker == null) return;
			MergeInto(entry, tracker.Records);
		}

		public static void CaptureFromSiteParts(Site site, StrikeAftermathEntry entry)
		{
			if (site?.parts == null || entry == null) return;
			for (int i = 0; i < site.parts.Count; i++)
			{
				SitePart part = site.parts[i];
				if (part == null) continue;

				Thing causer = part.conditionCauser;
				if (causer != null && causer.Destroyed)
					AddRecord(entry, FromThing(causer));

				if (part.things != null)
				{
					for (int t = 0; t < part.things.Count; t++)
					{
						Thing thing = part.things[t];
						if (thing != null && thing.Destroyed)
							AddRecord(entry, FromThing(thing));
					}
				}
			}
		}

		public static void CaptureAll(Map map, Site site, StrikeAftermathEntry entry)
		{
			if (entry.questDestroyed == null)
				entry.questDestroyed = new List<QuestDestroyedRecord>();
			CaptureFromMap(map, entry);
			CaptureFromSiteParts(site, entry);
		}

		/// <summary>
		/// Hostage (etc.) destroyed during strike → our End fails quests + removes Site.
		/// Terminals: leave to vanilla Ideo (Destroyed→Fail even after Hacked; Success only if terminal survives to MapRemoved).
		/// </summary>
		public static bool HasCriticalObjectiveDestroyed(Map map, Site site)
		{
			MapComponent_QuestDestroyTracker tracker = map?.GetComponent<MapComponent_QuestDestroyTracker>();
			if (tracker?.Records != null)
			{
				for (int i = 0; i < tracker.Records.Count; i++)
				{
					if (IsCriticalRecord(tracker.Records[i]))
						return true;
				}
			}

			if (site?.parts == null) return false;
			for (int i = 0; i < site.parts.Count; i++)
			{
				SitePart part = site.parts[i];
				if (part == null) continue;

				Thing causer = part.conditionCauser;
				if (causer != null && causer.Destroyed && IsCriticalDestroyedThing(causer))
					return true;

				if (part.things == null) continue;
				for (int t = 0; t < part.things.Count; t++)
				{
					Thing thing = part.things[t];
					if (thing == null || !thing.Destroyed) continue;
					if (IsCriticalDestroyedThing(thing))
						return true;
				}
			}

			return false;
		}

		/// <summary>Strike-End force-Fail only for pawns / non-hackable SitePart objectives. CompHackable → vanilla quest.</summary>
		public static bool IsCriticalDestroyedThing(Thing t)
		{
			if (t == null) return false;
			if (t is Pawn)
				return true;
			if (t.TryGetComp<CompHackable>() != null)
				return false;
			return IsSignificant(t);
		}

		public static bool IsCriticalRecord(QuestDestroyedRecord r)
		{
			if (r == null || r.defName.NullOrEmpty()) return false;
			return r.wasPawn;
		}

		public static void Apply(Map map)
		{
			if (map?.Parent == null) return;
			StrikeAftermathEntry entry = WorldComponent_StrikeAftermath.Get()?.GetByWorldObject(map.Parent);
			if (entry?.questDestroyed == null || entry.questDestroyed.Count == 0) return;

			CullMatching(map, entry.questDestroyed);
		}

		static void CullMatching(Map map, List<QuestDestroyedRecord> records)
		{
			List<Thing> doomed = new List<Thing>();
			List<Thing> buildings = map.listerThings?.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
			if (buildings != null)
			{
				for (int i = 0; i < buildings.Count; i++)
				{
					Thing t = buildings[i];
					if (t == null || t.Destroyed) continue;
					if (MatchesAny(t, records))
						doomed.Add(t);
				}
			}

			var pawns = map.mapPawns?.AllPawnsSpawned;
			if (pawns != null)
			{
				for (int i = 0; i < pawns.Count; i++)
				{
					Pawn p = pawns[i];
					if (p == null || p.Destroyed || p.Dead) continue;
					if (p.Faction == Faction.OfPlayer) continue;
					if (MatchesAny(p, records))
						doomed.Add(p);
				}
			}

			for (int i = 0; i < doomed.Count; i++)
			{
				Thing t = doomed[i];
				if (t == null || t.Destroyed) continue;
				try
				{
					if (t is Pawn p && !p.Dead)
						p.Kill(null);
					else
						t.Destroy(DestroyMode.Vanish);
				}
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] QuestDestroyedLedger cull: {e.Message}");
				}
			}
		}

		public static bool MatchesAny(Thing t, List<QuestDestroyedRecord> records)
		{
			if (t?.def == null || records == null) return false;
			string dn = t.def.defName;
			for (int i = 0; i < records.Count; i++)
			{
				QuestDestroyedRecord r = records[i];
				if (r == null || r.defName.NullOrEmpty()) continue;
				if (r.defName != dn) continue;
				if (r.wasPawn != t is Pawn) continue;
				if (r.wasHackable && t.TryGetComp<CompHackable>() == null) continue;
				if (r.questTags != null && r.questTags.Count > 0)
				{
					if (t.questTags == null || t.questTags.Count == 0) continue;
					if (!TagsOverlap(r.questTags, t.questTags)) continue;
				}
				return true;
			}
			return false;
		}

		static bool TagsOverlap(List<string> a, List<string> b)
		{
			for (int i = 0; i < a.Count; i++)
			{
				if (a[i].NullOrEmpty()) continue;
				for (int j = 0; j < b.Count; j++)
				{
					if (string.Equals(a[i], b[j], StringComparison.OrdinalIgnoreCase))
						return true;
				}
			}
			return false;
		}

		public static bool IsSignificant(Thing t)
		{
			if (t == null || t.Destroyed) return false;
			if (t.Faction == Faction.OfPlayer) return false;
			if (t is Pawn p)
			{
				if (QuestUtility.IsQuestLodger(p)) return true;
				if (p.questTags != null && p.questTags.Count > 0) return true;
				return false;
			}
			if (t.TryGetComp<CompHackable>() != null) return true;
			if (t.questTags != null && t.questTags.Count > 0) return true;
			return false;
		}

		public static QuestDestroyedRecord FromThing(Thing t)
		{
			var rec = new QuestDestroyedRecord
			{
				defName = t.def?.defName,
				wasPawn = t is Pawn,
				wasHackable = t.TryGetComp<CompHackable>() != null
			};
			if (t.questTags != null)
			{
				for (int i = 0; i < t.questTags.Count; i++)
				{
					if (!t.questTags[i].NullOrEmpty())
						rec.questTags.Add(t.questTags[i]);
				}
			}
			return rec;
		}

		static void MergeInto(StrikeAftermathEntry entry, IReadOnlyList<QuestDestroyedRecord> src)
		{
			if (src == null) return;
			if (entry.questDestroyed == null)
				entry.questDestroyed = new List<QuestDestroyedRecord>();
			for (int i = 0; i < src.Count; i++)
				AddRecord(entry, src[i]);
		}

		static void AddRecord(StrikeAftermathEntry entry, QuestDestroyedRecord rec)
		{
			if (rec == null || rec.defName.NullOrEmpty()) return;
			if (entry.questDestroyed == null)
				entry.questDestroyed = new List<QuestDestroyedRecord>();
			for (int i = 0; i < entry.questDestroyed.Count; i++)
			{
				QuestDestroyedRecord e = entry.questDestroyed[i];
				if (e != null && e.defName == rec.defName && e.wasPawn == rec.wasPawn
				    && e.wasHackable == rec.wasHackable)
					return;
			}
			entry.questDestroyed.Add(rec);
		}
	}

	public class MapComponent_QuestDestroyTracker : MapComponent
	{
		List<QuestDestroyedRecord> records = new List<QuestDestroyedRecord>();

		public MapComponent_QuestDestroyTracker(Map map) : base(map) { }

		public IReadOnlyList<QuestDestroyedRecord> Records => records;

		public static MapComponent_QuestDestroyTracker Ensure(Map map)
		{
			if (map == null) return null;
			MapComponent_QuestDestroyTracker comp = map.GetComponent<MapComponent_QuestDestroyTracker>();
			if (comp == null)
			{
				comp = new MapComponent_QuestDestroyTracker(map);
				map.components.Add(comp);
			}
			return comp;
		}

		public void RecordDestroyed(Thing t)
		{
			if (!QuestDestroyedLedger.IsSignificant(t)) return;
			QuestDestroyedRecord rec = QuestDestroyedLedger.FromThing(t);
			if (rec.defName.NullOrEmpty()) return;
			for (int i = 0; i < records.Count; i++)
			{
				QuestDestroyedRecord e = records[i];
				if (e != null && e.defName == rec.defName && e.wasPawn == rec.wasPawn
				    && e.wasHackable == rec.wasHackable)
					return;
			}
			records.Add(rec);
		}

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Collections.Look(ref records, "questDestroyed", LookMode.Deep);
			if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				if (records == null)
					records = new List<QuestDestroyedRecord>();
				records.RemoveAll(r => r == null || r.defName.NullOrEmpty());
			}
		}
	}
}
