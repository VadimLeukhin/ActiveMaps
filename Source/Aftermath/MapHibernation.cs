using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	public enum SiteFactKind : byte
	{
		None = 0,
		/// <summary>Deferred map spawn / state — apply on wake.</summary>
		RaidDue = 1,
		HostileNow = 2,
		/// <summary>Free-form note / future quest signal.</summary>
		Custom = 3
	}

	/// <summary>World-time fact queued while a map-bound site sleeps (C2).</summary>
	public class SiteFact : IExposable
	{
		public SiteFactKind kind;
		public int dueTick = -1;
		public bool ready;
		public bool applied;
		public string note;
		public float points;
		public string dedupeKey;

		public void ExposeData()
		{
			Scribe_Values.Look(ref kind, "kind", SiteFactKind.None);
			Scribe_Values.Look(ref dueTick, "due", -1);
			Scribe_Values.Look(ref ready, "ready", false);
			Scribe_Values.Look(ref applied, "applied", false);
			Scribe_Values.Look(ref note, "note");
			Scribe_Values.Look(ref points, "points", 0f);
			Scribe_Values.Look(ref dedupeKey, "dedupe");
		}
	}

	/// <summary>
	/// Map-bound Preserve: keep map in RAM (pin) but skip Map/Thing ticks until a living
	/// player pawn or an active strike is on it. Camera/CurrentMap alone does not wake.
	/// World/quest time still advances; facts queue on the entry.
	/// </summary>
	public static class MapHibernation
	{
		/// <summary>When false, map-bound End falls back to full-tick Hold (C3 / debug).</summary>
		public static bool PreferHibernate
			=> ActiveMapsSettingsUtil.Get().PreferHibernate;

		public static bool ForbidStrikeOnFragile
			=> ActiveMapsSettingsUtil.Get().ForbidStrikeOnFragile;

		/// <summary>Map-bound denylist site (distress etc.) — settings ForbidStrike blocks fire.</summary>
		public static bool IsFragileMapBoundSite(Site site, Map map = null)
		{
			if (site == null) return false;
			Map m = map ?? site.Map;
			if (EmergencyDenylist.Resolve(site, m) == EmergencyDenylist.Action.HoldMap)
				return true;
			return PreservePeekSafety.ShouldHoldOnPeek(site, m);
		}

		public static bool IsFragileMapBoundTile(PlanetTile tile)
		{
			if (!tile.Valid || Find.WorldObjects == null) return false;
			return Find.WorldObjects.MapParentAt(tile) is Site site && IsFragileMapBoundSite(site);
		}

		/// <summary>Fast path for Thing.DoTick — zero when no hibernating entries.</summary>
		public static int HibernatingEntryCount;

		/// <summary>Maps that were skipping ticks last PreTick — edge-detect wake → apply facts.</summary>
		static readonly HashSet<int> WasSkipping = new HashSet<int>();

		public static bool IsEntryHibernating(StrikeAftermathEntry e)
			=> e != null && e.hibernating;

		public static void RecountHibernating(List<StrikeAftermathEntry> entries)
		{
			int n = 0;
			if (entries != null)
			{
				for (int i = 0; i < entries.Count; i++)
				{
					if (entries[i] != null && entries[i].hibernating)
						n++;
				}
			}
			HibernatingEntryCount = n;
		}

		public static bool ShouldBeAwake(Map map)
		{
			if (map == null) return false;
			// Not Find.CurrentMap — map tabs would permanently run ticks / apply wake facts.
			if (FloorEntranceUtility.HasLivingPlayerOnMapOrLinkedFloors(map)) return true;
			if (RailgunStrikeZone.IsActiveMap(map)) return true;
			if (OutpostStrikeZone.IsActiveMap(map)) return true;
			return false;
		}

		/// <summary>Skip MapPre/PostTick and Thing.DoTick for this map.</summary>
		public static bool ShouldSkipTicks(Map map)
		{
			if (HibernatingEntryCount <= 0 || map?.Parent == null) return false;
			if (ShouldBeAwake(map)) return false;
			StrikeAftermathEntry e = WorldComponent_StrikeAftermath.Get()?.GetByWorldObject(map.Parent);
			return IsEntryHibernating(e);
		}

		public static void Enter(Map map, Site site)
		{
			if (map == null) return;
			ActiveMaps.Pin(map, WorldComponent_StrikeAftermath.PinHibernateMap, ActiveMapReason.LootWindow);
			var wc = WorldComponent_StrikeAftermath.Get();
			if (wc != null && site != null)
			{
				StrikeAftermathEntry e = wc.GetOrCreate(site, StrikeAftermathMode.PreserveHold);
				e.holdMapPinned = true;
				if (!e.hibernating)
					HibernatingEntryCount++;
				e.hibernating = true;
				if (e.siteFacts == null)
					e.siteFacts = new List<SiteFact>();
				SiteTimerMirror.CaptureOnHibernate(site, e);
			}
			WasSkipping.Add(map.uniqueID);
			Log.Message($"[CrystallizeActiveMaps] Hibernate map {map.uniqueID} ({site?.LabelCap ?? map.Parent?.LabelCap}).");
		}

		/// <summary>Full-tick Hold fallback (C3 / PreferHibernate=false).</summary>
		public static void EnterHoldFallback(Map map, Site site)
		{
			if (map == null) return;
			ActiveMaps.Pin(map, WorldComponent_StrikeAftermath.PinHoldMap, ActiveMapReason.LootWindow);
			var wc = WorldComponent_StrikeAftermath.Get();
			if (wc != null && site != null)
			{
				StrikeAftermathEntry e = wc.GetOrCreate(site, StrikeAftermathMode.PreserveHold);
				e.holdMapPinned = true;
				if (e.hibernating)
					HibernatingEntryCount = Math.Max(0, HibernatingEntryCount - 1);
				e.hibernating = false;
			}
		}

		public static void ClearHibernation(StrikeAftermathEntry e)
		{
			if (e == null) return;
			if (e.hibernating)
				HibernatingEntryCount = Math.Max(0, HibernatingEntryCount - 1);
			e.hibernating = false;
		}

		public static void NotifyMapPreTick(Map map)
		{
			if (map == null) return;
			int id = map.uniqueID;
			bool skip = ShouldSkipTicks(map);
			if (!skip && WasSkipping.Contains(id))
			{
				WasSkipping.Remove(id);
				OnWoke(map);
			}
			else if (skip)
				WasSkipping.Add(id);
		}

		static void OnWoke(Map map)
		{
			StrikeAftermathEntry e = WorldComponent_StrikeAftermath.Get()?.GetByWorldObject(map.Parent);
			if (e == null) return;
			SiteFactQueue.ApplyReady(e, map);
			Log.Message($"[CrystallizeActiveMaps] Unhibernate map {map.uniqueID} ({map.Parent?.LabelCap}).");
		}

		public static void EnqueueFact(StrikeAftermathEntry e, SiteFactKind kind, int dueTick,
			string note = null, float points = 0f, string dedupeKey = null)
		{
			if (e == null) return;
			if (e.siteFacts == null)
				e.siteFacts = new List<SiteFact>();
			if (!dedupeKey.NullOrEmpty())
			{
				for (int i = 0; i < e.siteFacts.Count; i++)
				{
					SiteFact existing = e.siteFacts[i];
					if (existing != null && !existing.applied
					    && existing.dedupeKey == dedupeKey)
					{
						existing.dueTick = dueTick;
						existing.note = note;
						existing.points = points;
						existing.kind = kind;
						if (dueTick >= 0 && Find.TickManager != null
						    && Find.TickManager.TicksGame >= dueTick)
							existing.ready = true;
						return;
					}
				}
			}

			var fact = new SiteFact
			{
				kind = kind,
				dueTick = dueTick,
				note = note,
				points = points,
				dedupeKey = dedupeKey
			};
			if (dueTick >= 0 && Find.TickManager != null
			    && Find.TickManager.TicksGame >= dueTick)
				fact.ready = true;
			e.siteFacts.Add(fact);
		}
	}

	/// <summary>Advance due facts on world tick; apply map-side effects on unhibernate.</summary>
	public static class SiteFactQueue
	{
		public static void TickEntries(List<StrikeAftermathEntry> entries)
		{
			if (entries == null || entries.Count == 0) return;
			int now = Find.TickManager?.TicksGame ?? 0;
			for (int i = 0; i < entries.Count; i++)
			{
				StrikeAftermathEntry e = entries[i];
				if (e?.siteFacts == null || e.siteFacts.Count == 0) continue;
				for (int f = 0; f < e.siteFacts.Count; f++)
				{
					SiteFact fact = e.siteFacts[f];
					if (fact == null || fact.applied || fact.ready) continue;
					if (fact.dueTick >= 0 && now >= fact.dueTick)
						fact.ready = true;
				}
			}
		}

		public static void ApplyReady(StrikeAftermathEntry e, Map map)
		{
			if (e?.siteFacts == null) return;
			for (int i = 0; i < e.siteFacts.Count; i++)
			{
				SiteFact fact = e.siteFacts[i];
				if (fact == null || fact.applied || !fact.ready) continue;
				try
				{
					ApplyOne(e, map, fact);
				}
				catch (Exception ex)
				{
					Log.Warning($"[CrystallizeActiveMaps] SiteFact {fact.kind}: {ex.Message}");
				}
				fact.applied = true;
			}
			e.siteFacts.RemoveAll(f => f == null || f.applied);
		}

		static void ApplyOne(StrikeAftermathEntry e, Map map, SiteFact fact)
		{
			if (fact.kind == SiteFactKind.None) return;
			Site site = map?.Parent as Site;
			switch (fact.kind)
			{
				case SiteFactKind.RaidDue:
					SiteFactApplicators.ApplyRaidDue(map, site, fact.points, fact.note);
					break;
				case SiteFactKind.HostileNow:
					Log.Message(
						$"[CrystallizeActiveMaps] SiteFact HostileNow on {map?.Parent?.LabelCap}" +
						(fact.note.NullOrEmpty() ? "" : $" ({fact.note})"));
					break;
				default:
					Log.Message(
						$"[CrystallizeActiveMaps] SiteFact {fact.kind} on {map?.Parent?.LabelCap}" +
						(fact.note.NullOrEmpty() ? "" : $" ({fact.note})"));
					break;
			}
		}
	}
}
