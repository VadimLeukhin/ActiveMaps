using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Ideology Hack_WorshippedTerminal: Peek Unload must not send site.MapRemoved while the
	/// objective terminal is still unhacked (that signal Fails the quest).
	/// After Hacked, MapRemoved must fire → Success (Hacked + MapRemoved), terminal still intact.
	/// Destroying the terminal (even after Hacked) is vanilla Fail — we do not suppress that.
	/// </summary>
	public static class QuestMapRemovedSuppress
	{
		static int depth;
		static MapParent target;

		public static bool IsActiveFor(MapParent parent)
			=> depth > 0 && parent != null && ReferenceEquals(parent, target);

		public static IDisposable MaybeEnter(Map map)
		{
			MapParent parent = map?.Parent;
			if (parent == null || !NeedsSuppress(parent))
				return Noop.Instance;

			depth++;
			if (depth == 1)
				target = parent;
			return new Popper();
		}

		static bool NeedsSuppress(MapParent parent)
		{
			var wc = WorldComponent_StrikeAftermath.Get();
			StrikeAftermathEntry entry = wc?.GetByWorldObject(parent);
			if (entry?.loot == null) return false;

			for (int i = 0; i < entry.loot.Count; i++)
			{
				Thing t = entry.loot[i];
				if (t == null || t.Destroyed) continue;
				if (!QuestSignificantSet.IsQuestCriticalHackable(t)) continue;
				CompHackable hack = t.TryGetComp<CompHackable>();
				if (hack != null && !hack.IsHacked)
					return true;
			}
			return false;
		}

		static void Exit()
		{
			if (depth <= 0) return;
			depth--;
			if (depth == 0)
				target = null;
		}

		struct Popper : IDisposable
		{
			public void Dispose() => Exit();
		}

		sealed class Noop : IDisposable
		{
			public static readonly Noop Instance = new Noop();
			public void Dispose() { }
		}
	}

	[HarmonyPatch(typeof(MapParent), nameof(MapParent.Notify_MyMapRemoved))]
	public static class Patch_MapParent_Notify_MyMapRemoved_Suppress
	{
		static bool Prefix(MapParent __instance)
		{
			if (!QuestMapRemovedSuppress.IsActiveFor(__instance))
				return true;

			// Still run WorldObjectComp.PostMyMapRemoved — skip only quest MapRemoved signals.
			try
			{
				var comps = __instance.AllComps;
				if (comps != null)
				{
					for (int i = 0; i < comps.Count; i++)
						comps[i].PostMyMapRemoved();
				}
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] MapRemoved suppress comps: {e.Message}");
			}

			Log.Message(
				$"[CrystallizeActiveMaps] Suppressed quest MapRemoved for {__instance.LabelCap} " +
				$"(unhacked terminal evacuated — quest stays ongoing).");
			return false;
		}
	}
}
