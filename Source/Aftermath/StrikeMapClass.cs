using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	public enum StrikeMapClass
	{
		Unknown = 0,
		Habitation,
		PreserveSite,
		EphemeralEmpty,
		Ruins,
		PlayerOwned
	}

	public static class StrikeMapClassifier
	{
		static readonly List<StrikeMapClassHook> hooks = new List<StrikeMapClassHook>();

		public static void RegisterHook(StrikeMapClassHook hook)
		{
			if (hook == null) return;
			if (!hooks.Contains(hook))
				hooks.Add(hook);
		}

		public static void UnregisterHook(StrikeMapClassHook hook)
		{
			if (hook == null) return;
			hooks.Remove(hook);
		}

		public static StrikeMapClass Classify(MapParent parent)
		{
			if (parent == null) return StrikeMapClass.Unknown;
			if (parent.Faction == Faction.OfPlayer && parent is Settlement)
				return StrikeMapClass.PlayerOwned;
			if (Find.AnyPlayerHomeMap != null && parent == Find.AnyPlayerHomeMap.Parent)
				return StrikeMapClass.PlayerOwned;

			if (parent is DestroyedSettlement)
				return StrikeMapClass.Ruins;

			// Per-call snapshot — shared static scratch is not reentrancy-safe.
			var snap = new List<StrikeMapClassHook>(hooks.Count);
			for (int i = 0; i < hooks.Count; i++)
				snap.Add(hooks[i]);

			for (int i = 0; i < snap.Count; i++)
			{
				StrikeMapClassHook hook = snap[i];
				if (hook == null) continue;
				try
				{
					if (hook(parent, out StrikeMapClass custom))
						return custom;
				}
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] StrikeMapClassHook failed: {e.Message}");
				}
			}

			// AbstractOutpost is ours but not a disposable wilderness strike host.
			if (parent is AbstractOutpost)
				return StrikeMapClass.Unknown;

			// DMS / Ancient Corps sites & Company WO — Habitation (VG/salvage), before
			// quest heuristics (LogisticTerminal contains "Terminal" → false Preserve).
			if (IsDmsHabitationParent(parent))
				return StrikeMapClass.Habitation;

			// Empty-tile strike host: our ActiveMapSite, or a blank foreign shell
			// (vanilla/mod encounter MapParent that is not a story Site with parts).
			if (IsEphemeralStrikeHost(parent))
				return StrikeMapClass.EphemeralEmpty;

			if (parent is Settlement)
				return StrikeMapClass.Habitation;

			// Biotech+/Odyssey camp WO — same Habitation matrix as Settlement.
			if (parent is Camp)
				return StrikeMapClass.Habitation;

			if (parent is Site site)
			{
				// Quest/story/condition Sites stay Preserve.
				if (IsQuestOrStoryPreserveSite(site))
					return StrikeMapClass.PreserveSite;
				// VOE outposts, logging/hunting/mining supply camps, bandit camps, etc.
				if (IsWorkCampOrFactionOutpostSite(site))
					return StrikeMapClass.Habitation;
				// Unknown Site with parts — Preserve (safer than salvage/destroy).
				return StrikeMapClass.PreserveSite;
			}

			// Fallback: hostile faction base-like MapParent without being a Site.
			if (parent.Faction != null && parent.Faction.HostileTo(Faction.OfPlayer)
			    && parent.def != null && parent.def.canHaveFaction)
				return StrikeMapClass.Habitation;

			// Unknown disposable-looking parent on wilderness — prefer unload+destroy over Preserve forever.
			if (IsLikelyDisposableMapParent(parent))
				return StrikeMapClass.EphemeralEmpty;

			return StrikeMapClass.PreserveSite;
		}

		/// <summary>
		/// True for hosts that exist only to hold a strike/encounter map and should be destroyed on End.
		/// </summary>
		public static bool IsEphemeralStrikeHost(MapParent parent)
		{
			if (parent == null || parent is AbstractOutpost) return false;
			if (parent is ActiveMapSite) return true;

			// Foreign blank Site: no SiteParts → not a quest/story site; safe to treat as temp.
			if (parent is Site site)
			{
				if (site.parts != null && site.parts.Count > 0) return false;
				return true;
			}

			return false;
		}

		/// <summary>
		/// DMS Ancient Corps Site / Company MapParents — treat as Habitation for VG/salvage.
		/// Multi-floor layouts: ledger/sketch are 2D-only; maps with floor entrances
		/// (MapPortal / def.portal) use keep-linked-floors instead of unload (see FloorEntranceUtility).
		/// </summary>
		public static bool IsDmsHabitationParent(MapParent parent)
		{
			if (parent == null) return false;
			string defName = parent.def?.defName ?? "";
			if (ContainsAny(defName, "DMS_", "DMSAC_", "DeadMan"))
				return true;

			Type t = parent.GetType();
			string typeName = t?.Name ?? "";
			string ns = t?.Namespace ?? "";
			if (ContainsAny(ns, "AncientCorps", "DeadManSwitch", "DMS"))
			{
				if (ContainsAny(typeName, "Company", "Site", "Garrison", "Outpost", "Facility"))
					return true;
			}

			if (parent is Site site)
				return IsDmsHabitationSite(site);
			return false;
		}

		public static bool IsDmsHabitationSite(Site site)
		{
			if (site?.parts == null) return false;
			for (int i = 0; i < site.parts.Count; i++)
			{
				SitePart part = site.parts[i];
				string n = part?.def?.defName ?? "";
				string worker = part?.def?.workerClass?.FullName ?? part?.def?.workerClass?.Name ?? "";
				string combined = n + " " + worker;
				if (ContainsAny(combined, "DMS_", "DMSAC_", "AncientCorps", "DeadManSwitch"))
					return true;
			}
			return false;
		}

		/// <summary>
		/// Quest / condition-causer / story Sites — do not Salvage or Destroy from Aftermath.
		/// </summary>
		public static bool IsQuestOrStoryPreserveSite(Site site)
		{
			if (site == null) return false;
			if (SiteHasConditionCauserPart(site)) return true;
			if (SiteTouchedByOngoingQuest(site)) return true;
			if (site.parts == null) return false;
			for (int i = 0; i < site.parts.Count; i++)
			{
				string n = site.parts[i]?.def?.defName ?? "";
				if (n.Length == 0) continue;
				if (ContainsAny(n,
					"Quest", "Ancient", "Ritual", "Prisoner", "Hostage", "Kidnap",
					"Hack", "Terminal", "Mechanoid", "Insect", "Toxic", "Condition",
					"Story", "Relic", "Monument", "ShuttleCrash", "ItemStash"))
					return true;
			}
			return false;
		}

		/// <summary>
		/// Faction work camps / VOE-style outposts that should use Habitation (VG / Salvage), not Preserve.
		/// </summary>
		public static bool IsWorkCampOrFactionOutpostSite(Site site)
		{
			if (site?.parts == null || site.parts.Count == 0) return false;
			for (int i = 0; i < site.parts.Count; i++)
			{
				SitePart part = site.parts[i];
				string n = part?.def?.defName ?? "";
				string worker = part?.def?.workerClass?.Name ?? "";
				string combined = n + " " + worker;
				if (ContainsAny(combined,
					"Logging", "Hunting", "Mining", "Farm", "Farming", "Quarry",
					"Fishing", "Gather", "WorkSite", "SupplyCamp", "BanditCamp",
					"Outpost", "VOE_", "VEE_Outpost", "OutpostsExpanded",
					"Camp", "RaiderCamp", "PirateCamp", "SlaverCamp"))
				{
					// "Camp" alone is broad but matches LoggingCamp/HuntingCamp; story parts
					// already filtered by IsQuestOrStoryPreserveSite when called first.
					return true;
				}
			}
			return false;
		}

		static bool SiteHasConditionCauserPart(Site site)
		{
			if (site.parts == null) return false;
			for (int i = 0; i < site.parts.Count; i++)
			{
				if (site.parts[i]?.def?.conditionCauserDef != null)
					return true;
			}
			return false;
		}

		static bool SiteTouchedByOngoingQuest(Site site)
		{
			if (Find.QuestManager == null) return false;
			foreach (Quest q in Find.QuestManager.QuestsListForReading)
			{
				if (q == null || q.State != QuestState.Ongoing) continue;
				foreach (QuestPart part in q.PartsListForReading)
				{
					if (part == null) continue;
					try
					{
						FieldInfo[] fields = QuestPartFieldCache.For(part);
						for (int fi = 0; fi < fields.Length; fi++)
						{
							object val;
							try { val = fields[fi].GetValue(part); } catch { continue; }
							if (ReferenceEquals(val, site)) return true;
							if (val is PlanetTile pt && site.Tile.Valid && pt == site.Tile) return true;
						}
					}
					catch { /* ignore bad parts */ }
				}
			}
			return false;
		}

		static bool ContainsAny(string haystack, params string[] needles)
		{
			if (haystack.NullOrEmpty()) return false;
			for (int i = 0; i < needles.Length; i++)
			{
				if (haystack.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
					return true;
			}
			return false;
		}
		/// <summary>Public for Aftermath destroy gate — Ambush/Temp/Encounter-style parents.</summary>
		public static bool IsLikelyDisposableMapParent(MapParent parent)
		{
			if (parent == null) return false;
			if (parent is Settlement || parent is Camp || parent is DestroyedSettlement) return false;
			if (parent is Site) return false; // handled by IsEphemeralStrikeHost / Preserve
			if (parent.Faction != null) return false;
			string defName = parent.def?.defName ?? "";
			if (defName.IndexOf("Ambush", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (defName.IndexOf("Attacked", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (defName.IndexOf("Temp", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (defName.IndexOf("Encounter", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}

		/// <summary>
		/// Vanilla SettlementDefeatUtility.IsDefeated only counts Humanlike threats —
		/// mech / insect / animal bases always look defeated. For Active Maps salvage we
		/// treat any active threat of the site faction as still holding the habitation.
		/// </summary>
		public static bool IsHabitationDefeated(Map map, MapParent parent)
		{
			if (map == null || parent?.Faction == null) return false;
			try
			{
				Faction faction = parent.Faction;
				bool settlementParent = map.Parent is Settlement && map.Parent.Faction == faction;
				List<Pawn> list = map.mapPawns.SpawnedPawnsInFaction(faction);
				for (int i = 0; i < list.Count; i++)
				{
					Pawn pawn = list[i];
					if (pawn == null || pawn.Dead) continue;
					if (GenHostility.IsActiveThreatToPlayer(pawn, settlementParent))
						return false;
				}
				return true;
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] IsHabitationDefeated: {e.Message}");
				return false;
			}
		}
	}

	public struct StrikeEndContext
	{
		public bool AnyShotFired;
		public string PinTokenMission;
		public string PinTokenLoot;

		/// <summary>Optional: clear site condition causers before unload (railgun mid-strike tracking).</summary>
		public Action<Map, MapParent> ClearConditions;
	}
}
