using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace Crystallize.ActiveMaps
{
	public enum PresenceMode
	{
		/// <summary>No map; stash only; minimal tick.</summary>
		Stash = 0,
		/// <summary>No map; light sim (ammo, timers, fire requests).</summary>
		Simulate = 1,
		/// <summary>Full map loaded (raid only for player outposts; or strike target).</summary>
		Map = 2
	}

	/// <summary>Ledger-first building/ammo counts while in Mode1/2.</summary>
	public class BuildingLedger : IExposable
	{
		private Dictionary<string, int> counts = new Dictionary<string, int>();

		/// <summary>
		/// Copy taken at LoadingVars. Save-opt mods may clear/reinit collections during
		/// ResolvingCrossRefs; we restore from this instead of re-running Look (which
		/// would empty or double-apply Value/Value data).
		/// </summary>
		[Unsaved]
		Dictionary<string, int> loadSnapshot;

		public int Get(string defName)
		{
			EnsureCounts();
			if (defName.NullOrEmpty()) return 0;
			return counts.TryGetValue(defName, out int n) ? n : 0;
		}

		public void Set(string defName, int value)
		{
			EnsureCounts();
			if (defName.NullOrEmpty()) return;
			if (value <= 0) counts.Remove(defName);
			else counts[defName] = value;
		}

		public void Add(string defName, int delta)
		{
			if (defName.NullOrEmpty() || delta == 0) return;
			Set(defName, Get(defName) + delta);
		}

		public bool TryConsume(string defName, int amount)
		{
			if (amount <= 0) return true;
			int have = Get(defName);
			if (have < amount) return false;
			Set(defName, have - amount);
			return true;
		}

		public IEnumerable<KeyValuePair<string, int>> All
		{
			get
			{
				EnsureCounts();
				return counts;
			}
		}

		public void ExposeData()
		{
			// LookMode.Value+Value is fully applied in LoadingVars. Do not call Look again
			// on ResolvingCrossRefs / PostLoadInit — a second pass (or a save-opt mod
			// re-entering ExposeData) can replace counts with empty or a second instance.
			if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.LoadingVars)
			{
				Scribe_Collections.Look(ref counts, "counts", LookMode.Value, LookMode.Value);
			}

			if (Scribe.mode == LoadSaveMode.LoadingVars)
			{
				Sanitize(ref counts);
				loadSnapshot = new Dictionary<string, int>(counts);
			}
			else if (Scribe.mode == LoadSaveMode.ResolvingCrossRefs
			         || Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				RestoreIfClobbered();
				if (Scribe.mode == LoadSaveMode.PostLoadInit)
					loadSnapshot = null;
			}

			EnsureCounts();
		}

		void RestoreIfClobbered()
		{
			bool emptied = counts == null || counts.Count == 0;
			bool hadData = loadSnapshot != null && loadSnapshot.Count > 0;
			if (emptied && hadData)
				counts = new Dictionary<string, int>(loadSnapshot);
			else
				Sanitize(ref counts);
		}

		static void Sanitize(ref Dictionary<string, int> dict)
		{
			if (dict == null)
			{
				dict = new Dictionary<string, int>();
				return;
			}
			bool dirty = false;
			foreach (KeyValuePair<string, int> kv in dict)
			{
				if (kv.Key.NullOrEmpty())
				{
					dirty = true;
					break;
				}
			}
			if (!dirty) return;
			var next = new Dictionary<string, int>(dict.Count);
			foreach (KeyValuePair<string, int> kv in dict)
			{
				if (kv.Key.NullOrEmpty()) continue;
				next[kv.Key] = kv.Value;
			}
			dict = next;
		}

		void EnsureCounts()
		{
			if (counts == null)
				counts = new Dictionary<string, int>();
		}
	}

	public interface IStashFoldHook
	{
		/// <summary>Return false to skip folding this pawn into stash (left on map / destroyed by caller).</summary>
		bool CanFoldPawn(Pawn pawn, AbstractOutpost outpost);

		/// <summary>Extra things to scoop during Demote (after default item scan).</summary>
		void CollectExtraThings(AbstractOutpost outpost, Map map, List<Thing> sink);

		void AfterFold(AbstractOutpost outpost);
	}

	public static class StashFoldHooks
	{
		static readonly List<IStashFoldHook> hooks = new List<IStashFoldHook>();
		static readonly DefaultStashFoldHook builtin = new DefaultStashFoldHook();

		static StashFoldHooks()
		{
			hooks.Add(builtin);
		}

		public static void Register(IStashFoldHook hook)
		{
			if (hook == null || ReferenceEquals(hook, builtin)) return;
			if (!hooks.Contains(hook))
				hooks.Add(hook);
		}

		public static void Unregister(IStashFoldHook hook)
		{
			if (hook == null || ReferenceEquals(hook, builtin)) return;
			hooks.Remove(hook);
		}

		public static bool CanFoldPawn(Pawn pawn, AbstractOutpost outpost)
		{
			// Per-call snapshot: shared static scratch is not reentrancy-safe
			// (nested CanFoldPawn / Unregister from a hook would Clear mid-loop).
			List<IStashFoldHook> snap = SnapshotHooks();
			for (int i = 0; i < snap.Count; i++)
			{
				IStashFoldHook h = snap[i];
				if (h == null) continue;
				try
				{
					if (!h.CanFoldPawn(pawn, outpost))
						return false;
				}
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] StashFoldHook.CanFoldPawn: {e.Message}");
				}
			}
			return true;
		}

		public static void CollectExtraThings(AbstractOutpost outpost, Map map, List<Thing> sink)
		{
			List<IStashFoldHook> snap = SnapshotHooks();
			for (int i = 0; i < snap.Count; i++)
			{
				IStashFoldHook h = snap[i];
				if (h == null) continue;
				try { h.CollectExtraThings(outpost, map, sink); }
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] StashFoldHook.CollectExtraThings: {e.Message}");
				}
			}
		}

		public static void AfterFold(AbstractOutpost outpost)
		{
			List<IStashFoldHook> snap = SnapshotHooks();
			for (int i = 0; i < snap.Count; i++)
			{
				IStashFoldHook h = snap[i];
				if (h == null) continue;
				try { h.AfterFold(outpost); }
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] StashFoldHook.AfterFold: {e.Message}");
				}
			}
		}

		static List<IStashFoldHook> SnapshotHooks()
		{
			var dst = new List<IStashFoldHook>(hooks.Count);
			for (int i = 0; i < hooks.Count; i++)
				dst.Add(hooks[i]);
			return dst;
		}
	}

	public class DefaultStashFoldHook : IStashFoldHook
	{
		/// <summary>Vehicle Framework base type. Null if VF is not loaded.</summary>
		static readonly Type vehiclePawnType = AccessTools.TypeByName("Vehicles.VehiclePawn");

		public bool CanFoldPawn(Pawn pawn, AbstractOutpost outpost)
		{
			if (pawn == null || pawn.Destroyed) return false;
			// VF vehicles inherit Vehicles.VehiclePawn. Do not gate on XML defName
			// (false positives on "Vehicle*" pawns, misses subclasses with other names).
			if (vehiclePawnType != null && vehiclePawnType.IsInstanceOfType(pawn))
				return false;
			return true;
		}

		public void CollectExtraThings(AbstractOutpost outpost, Map map, List<Thing> sink) { }

		public void AfterFold(AbstractOutpost outpost) { }
	}
}
