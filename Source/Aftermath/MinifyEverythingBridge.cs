using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>Soft Minify Everything bridge — no hard dependency.</summary>
	public static class MinifyEverythingBridge
	{
		static bool? available;

		public static bool IsAvailable()
		{
			if (available.HasValue) return available.Value;
			available = false;
			try
			{
				foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
				{
					string id = mod?.PackageIdPlayerFacing ?? mod?.PackageId ?? "";
					if (id.IndexOf("minify", StringComparison.OrdinalIgnoreCase) < 0
					    && id.IndexOf("MinifyEverything", StringComparison.OrdinalIgnoreCase) < 0)
						continue;
					available = true;
					break;
				}
				if (available != true)
				{
					// Also detect by type name in loaded assemblies.
					foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
					{
						Type[] types;
						try { types = asm.GetTypes(); }
						catch (ReflectionTypeLoadException ex) { types = ex.Types; }
						catch { continue; }
						if (types == null) continue;
						for (int i = 0; i < types.Length; i++)
						{
							Type x = types[i];
							if (x == null) continue;
							if (x.Name.IndexOf("Minify", StringComparison.OrdinalIgnoreCase) < 0) continue;
							available = true;
							break;
						}
						if (available == true) break;
					}
				}
			}
			catch
			{
				available = false;
			}
			return available == true;
		}

		/// <summary>
		/// Best-effort: uninstall/minify buildings the same way vanilla minifiable does when possible.
		/// Returns count of minified stacks created.
		/// </summary>
		public static int TryMinifyEligibleBuildings(Map map)
		{
			if (map == null || !IsAvailable()) return 0;
			int count = 0;
			try
			{
				List<Thing> buildings = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial).ToList();
				for (int i = 0; i < buildings.Count; i++)
				{
					if (!(buildings[i] is Building b) || b.Destroyed || !b.Spawned) continue;
					if (b.Faction == Faction.OfPlayer) continue;
					if (!b.def.Minifiable) continue;
					if (b.def.category != ThingCategory.Building) continue;
					// Skip condition causers / quest-tagged.
					if (b.questTags != null && b.questTags.Count > 0) continue;
					try
					{
						MinifiedThing mini = b.Uninstall();
						if (mini != null) count++;
					}
					catch
					{
						// Building refused uninstall — skip.
					}
				}
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] TryMinifyEligibleBuildings: {e.Message}");
			}
			return count;
		}
	}
}
