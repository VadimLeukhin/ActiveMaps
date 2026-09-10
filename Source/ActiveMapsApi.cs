using System;
using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Stable public API for third-party mods (packageId: <c>crystallize.activemaps</c>).
	/// Prefer this facade over calling internal helpers directly.
	/// <para>
	/// Soft dependency: resolve types by name or add a loadAfter on crystallize.activemaps.
	/// Check <see cref="ApiVersion"/> before using newer members.
	/// </para>
	/// <para>
	/// Pin tokens: use a unique string like <c>your.packageId.reason</c>.
	/// Tokens not in this mod's owned set are <b>foreign</b> — hard unload (DeinitAndRemoveMap)
	/// will abort while they remain. Always Unpin your own token when done.
	/// </para>
	/// </summary>
	public static class ActiveMapsApi
	{
		/// <summary>Bump when breaking / adding public surface. 3 = QuestVgLight default for all Preserve (registry removed).</summary>
		public const int ApiVersion = 3;

		public const string PackageId = "crystallize.activemaps";

		public static bool IsLoaded => true;

		// ── Map hold (no scout pawn) ─────────────────────────────────────────

		/// <summary>Load or generate the map for a world tile (may create Crystallize_ActiveMapSite).</summary>
		public static Map EnsureLoadedMap(PlanetTile tile, ActiveMapReason reason = ActiveMapReason.Other)
			=> ActiveMaps.EnsureLoadedMap(tile, reason);

		/// <summary>
		/// Keep the map in memory. Use a unique token; do not reuse framework owned tokens
		/// (rimatomics.*, activemaps.*). Foreign tokens block forced unload.
		/// </summary>
		public static void Pin(Map map, string token, ActiveMapReason reason = ActiveMapReason.Other)
			=> ActiveMaps.Pin(map, token, reason);

		public static void Unpin(Map map, string token)
			=> ActiveMaps.Unpin(map, token);

		public static bool IsPinned(Map map)
			=> ActiveMaps.IsPinned(map);

		/// <summary>True if any non-framework pin token still holds the map.</summary>
		public static bool HasForeignPins(Map map)
			=> ActiveMaps.HasForeignPins(map);

		public static string DescribePins(Map map)
			=> ActiveMaps.DescribePins(map);

		public static void JumpToMap(Map map)
			=> ActiveMaps.JumpToMap(map);

		/// <summary>
		/// Soft unload: drops only framework-owned pins, then Deinit if nothing foreign remains.
		/// Returns false if aborted due to foreign pins (map stays loaded).
		/// </summary>
		public static bool TryUnload(Map map)
			=> StrikeSalvageUtility.UnloadMap(map);

		// ── Outpost stash fold ───────────────────────────────────────────────

		/// <summary>Decide which pawns/things fold into AbstractOutpost stash on Demote.</summary>
		public static void RegisterStashFoldHook(IStashFoldHook hook)
			=> StashFoldHooks.Register(hook);

		public static void UnregisterStashFoldHook(IStashFoldHook hook)
			=> StashFoldHooks.Unregister(hook);

		// ── Outpost raid map sketch ──────────────────────────────────────────

		/// <summary>Called when an outpost is Promoted for raid and the map is ready.</summary>
		public static void RegisterRaidSketchProvider(Action<AbstractOutpost, Map> provider)
			=> OutpostRaidUtility.RegisterSketchProvider(provider);

		public static void UnregisterRaidSketchProvider(Action<AbstractOutpost, Map> provider)
			=> OutpostRaidUtility.UnregisterSketchProvider(provider);

		// ── Quest / site emergency denylist ──────────────────────────────────

		/// <summary>After remote strike: keep map loaded (do not evacuate/unload). Peek and shots.</summary>
		public static void RegisterQuestHoldMap(string questScriptDefName)
			=> EmergencyDenylist.RegisterQuestScript(questScriptDefName, EmergencyDenylist.Action.HoldMap);

		/// <summary>Unload map but skip quest-significant evacuate.</summary>
		public static void RegisterQuestSkipEvacuate(string questScriptDefName)
			=> EmergencyDenylist.RegisterQuestScript(questScriptDefName, EmergencyDenylist.Action.SkipEvacuateOnly);

		public static void UnregisterQuestScript(string questScriptDefName)
			=> EmergencyDenylist.UnregisterQuestScript(questScriptDefName);

		public static void RegisterSitePartHoldMap(string sitePartDefName)
			=> EmergencyDenylist.RegisterSitePart(sitePartDefName, EmergencyDenylist.Action.HoldMap);

		public static void RegisterSitePartSkipEvacuate(string sitePartDefName)
			=> EmergencyDenylist.RegisterSitePart(sitePartDefName, EmergencyDenylist.Action.SkipEvacuateOnly);

		public static void UnregisterSitePart(string sitePartDefName)
			=> EmergencyDenylist.UnregisterSitePart(sitePartDefName);

		// ── Strike map classification overrides ──────────────────────────────

		/// <summary>
		/// First hook that returns true wins (checked after player-home / ruins shortcuts).
		/// Use to mark your MapParent as Habitation / PreserveSite / EphemeralEmpty.
		/// </summary>
		public static void RegisterStrikeMapClassHook(StrikeMapClassHook hook)
			=> StrikeMapClassifier.RegisterHook(hook);

		public static void UnregisterStrikeMapClassHook(StrikeMapClassHook hook)
			=> StrikeMapClassifier.UnregisterHook(hook);
	}

	/// <summary>Return true and set <paramref name="result"/> to override default classification.</summary>
	public delegate bool StrikeMapClassHook(MapParent parent, out StrikeMapClass result);
}
