using System;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Map-bound Hold only (Anomaly distress). All other Preserve: Evacuate peek / QuestVgLight after shells.
	/// </summary>
	[StaticConstructorOnStartup]
	public static class PreservePeekSafety
	{
		static PreservePeekSafety()
		{
			// Anomaly distress — Unload alone silent-fails quest and destroys Site.
			EmergencyDenylist.RegisterQuestScript("OpportunitySite_DistressCall", EmergencyDenylist.Action.HoldMap);
			RegisterSitePartsHold(
				"DistressCall_Fleshbeasts",
				"DistressCall_Settlement",
				"DistressCall_Fleshmass",
				"DistressCall_FleshSacks",
				"DistressCall_Fleshbulbs",
				"DistressCall_PitBurrows",
				"DistressCall_BurntPatches");
		}

		static void RegisterSitePartsHold(params string[] defNames)
		{
			for (int i = 0; i < defNames.Length; i++)
				EmergencyDenylist.RegisterSitePart(defNames[i], EmergencyDenylist.Action.HoldMap);
		}

		/// <summary>
		/// Peek heuristic: only map-bound distress (mods that skipped RegisterQuestHoldMap).
		/// </summary>
		public static bool ShouldHoldOnPeek(Site site, Map map)
		{
			if (site == null) return false;
			return SiteLooksLikeDistressCall(site);
		}

		public static bool SiteLooksLikeDistressCall(Site site)
		{
			if (site?.parts == null) return false;
			for (int i = 0; i < site.parts.Count; i++)
			{
				string n = site.parts[i]?.def?.defName ?? "";
				string worker = site.parts[i]?.def?.workerClass?.Name ?? "";
				if (Contains(n, "DistressCall") || Contains(worker, "DistressCall"))
					return true;
			}
			return false;
		}

		static bool Contains(string hay, string needle)
		{
			if (hay.NullOrEmpty() || needle.NullOrEmpty()) return false;
			return hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
		}
	}
}
