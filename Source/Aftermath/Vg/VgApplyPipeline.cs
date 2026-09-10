using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace Crystallize.ActiveMaps
{
	/// <summary>Phase 1 apply pipeline — Habitation flags = former ApplyOnMapGenerated body.</summary>
	public static class VgApplyPipeline
	{
		public static void Run(Map map)
		{
			if (map?.Parent == null) return;
			var wc = WorldComponent_StrikeAftermath.Get();
			StrikeAftermathEntry entry = wc?.GetByWorldObject(map.Parent);
			if (entry == null || entry.mode != StrikeAftermathMode.VirtualGarrison) return;
			if (entry.ledgerApplied) return;
			// Kept multi-floor surface — never run regen cull/sketch on a fresh Generate.
			if (entry.keepLinkedFloors) return;

			VgProfile profile = VgProfileResolver.ResolveFromEntry(entry);
			Faction fac = map.Parent.Faction;

			using (FogRevealSuppress.Scope())
			{
				try
				{
					if (entry.garrison == null)
						entry.garrison = new List<Pawn>();
					entry.garrison.RemoveAll(p => p == null || p.Destroyed);

					// Restore runs in VirtualGarrisonUtility.ApplyOnMapGenerated (all modes).

					if (profile.Has(VgFlags.CullRegenHostiles))
						CullRegenHostiles(map, fac, entry);

					if (profile.Has(VgFlags.StructureSketch))
						StructureSketchUtility.Apply(map, fac, entry.structureSketch);

					var reinstated = new List<Pawn>();
					IntVec3 defendCenter = map.Center;
					if (profile.Has(VgFlags.FoldDefenders))
						reinstated = ReinstateGarrison(map, entry, out defendCenter);

					if (profile.Has(VgFlags.LordDefendBase) && reinstated.Count > 0 && fac != null)
						TryMakeDefendLord(map, fac, reinstated, defendCenter);

					if (profile.Has(VgFlags.DefenseLedger))
						ApplyDefenseLedger(map, fac, entry);

					entry.ledgerApplied = true;
					entry.garrison.Clear();
				}
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] VG Apply: {e.Message}");
				}
			}
		}

		static void CullRegenHostiles(Map map, Faction fac, StrikeAftermathEntry entry)
		{
			List<Pawn> spawned = map.mapPawns?.AllPawnsSpawned?.ToList() ?? new List<Pawn>();
			for (int i = 0; i < spawned.Count; i++)
			{
				Pawn p = spawned[i];
				if (p == null || p.Destroyed) continue;
				if (p.Faction != fac) continue;
				if (entry.garrison.Contains(p)) continue;
				try { p.Destroy(DestroyMode.Vanish); }
				catch
				{
					try { p.Kill(null); } catch { /* ignore */ }
				}
			}
		}

		static List<Pawn> ReinstateGarrison(Map map, StrikeAftermathEntry entry, out IntVec3 defendCenter)
		{
			bool useSafeSpawn = TryFindSafeGarrisonSpawnNear(map.Center, map, forPawn: null, out defendCenter);
			if (!useSafeSpawn)
				defendCenter = map.Center;
			int safeFailStreak = 0;
			const int SafeFailStreakAbort = 2;

			var reinstated = new List<Pawn>();
			for (int gi = 0; gi < entry.garrison.Count; gi++)
			{
				Pawn p = entry.garrison[gi];
				if (p.Spawned) continue;
				try
				{
					if (p.IsWorldPawn())
						Find.WorldPawns.RemovePawn(p);
					IntVec3 cell;
					if (useSafeSpawn && TryFindSafeGarrisonSpawnNear(defendCenter, map, p, out cell))
					{
						safeFailStreak = 0;
					}
					else
					{
						if (useSafeSpawn)
						{
							safeFailStreak++;
							if (safeFailStreak >= SafeFailStreakAbort)
								useSafeSpawn = false;
						}
						cell = CellFinder.RandomSpawnCellForPawnNear(defendCenter, map, 8);
					}
					GenSpawn.Spawn(p, cell, map, WipeMode.Vanish);
					PrepareReinstatedDefender(p);
					reinstated.Add(p);
				}
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] VG spawn: {e.Message}");
				}
			}

			return reinstated;
		}

		static void TryMakeDefendLord(Map map, Faction fac, List<Pawn> reinstated, IntVec3 defendCenter)
		{
			try
			{
				LordMaker.MakeNewLord(
					fac,
					new LordJob_DefendBase(fac, defendCenter, delayBeforeAssault: 0, attackWhenPlayerBecameEnemy: true),
					map,
					reinstated);
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] VG defend lord: {e.Message}");
			}
		}

		static readonly int[] GarrisonSpawnRadii = { 8, 16, 32 };
		const int GarrisonSafeCellMaxTries = 40;

		static bool IsSafeGarrisonCell(IntVec3 cell, Map map, Pawn forPawn)
		{
			if (!cell.InBounds(map)) return false;
			if (!cell.Standable(map)) return false;
			if (cell.ContainsStaticFire(map)) return false;
			if (forPawn != null && cell.GetDangerFor(forPawn, map) != Danger.None)
				return false;
			return true;
		}

		static bool TryFindSafeGarrisonSpawnNear(IntVec3 root, Map map, Pawn forPawn, out IntVec3 result)
		{
			if (forPawn != null)
			{
				if (CellFinder.TryFindRandomCellNear(
						root, map, 8,
						cell => IsSafeGarrisonCell(cell, map, forPawn),
						out result, GarrisonSafeCellMaxTries))
					return true;
				result = IntVec3.Invalid;
				return false;
			}

			for (int i = 0; i < GarrisonSpawnRadii.Length; i++)
			{
				if (CellFinder.TryFindRandomCellNear(
						root, map, GarrisonSpawnRadii[i],
						cell => IsSafeGarrisonCell(cell, map, forPawn),
						out result, GarrisonSafeCellMaxTries))
					return true;
			}

			result = IntVec3.Invalid;
			return false;
		}

		static void PrepareReinstatedDefender(Pawn p)
		{
			if (p == null) return;
			try
			{
				if (!PawnComponentsUtility.HasSpawnedComponents(p))
					PawnComponentsUtility.AddComponentsForSpawn(p);
			}
			catch { /* ignore */ }

			p.jobs?.StopAll(ifLayingKeepLaying: false, canReturnToPool: true);
			p.jobs?.ClearQueuedJobs(canReturnToPool: true);
			p.pather?.StopDead();
			try { p.mindState?.Reset(clearInspiration: false, clearMentalState: true); } catch { /* ignore */ }
		}

		static void ApplyDefenseLedger(Map map, Faction fac, StrikeAftermathEntry entry)
		{
			Dictionary<string, int> byDef = entry?.survivingDefenseByDef ?? new Dictionary<string, int>();
			ApplyDefenseLedgerByDef(map, fac, byDef);
			PurgeOrphanMortarShells(map, fac);
		}

		static void ApplyDefenseLedgerByDef(Map map, Faction fac, Dictionary<string, int> saved)
		{
			var onMap = new Dictionary<string, List<Thing>>();
			List<Thing> list = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
			for (int i = 0; i < list.Count; i++)
			{
				Thing t = list[i];
				if (!StrikeSalvageUtility.IsEnemyDefenseLedgerBuilding(t, fac)) continue;
				string key = t.def?.defName;
				if (key.NullOrEmpty()) continue;
				if (!onMap.TryGetValue(key, out List<Thing> bucket))
				{
					bucket = new List<Thing>();
					onMap[key] = bucket;
				}
				bucket.Add(t);
			}

			var doomed = new List<Thing>();
			foreach (KeyValuePair<string, List<Thing>> kv in onMap)
			{
				int keep = 0;
				if (saved.TryGetValue(kv.Key, out int n))
					keep = Math.Max(0, n);
				List<Thing> bucket = kv.Value;
				int extra = bucket.Count - keep;
				if (extra <= 0) continue;
				bucket.Shuffle();
				for (int i = 0; i < extra && i < bucket.Count; i++)
					doomed.Add(bucket[i]);
			}

			var allMortars = new List<Thing>();
			for (int i = 0; i < list.Count; i++)
			{
				Thing t = list[i];
				if (t == null || t.Destroyed || !t.Spawned) continue;
				if (!IsMortarLike(t)) continue;
				if (fac != null && t.Faction != fac) continue;
				allMortars.Add(t);
			}
			Dictionary<Thing, List<Thing>> shellsByMortar = AssignShellsToNearestMortar(map, allMortars);

			for (int i = 0; i < doomed.Count; i++)
				DestroyDefenseBuildingAndOwnedAmmo(doomed[i], shellsByMortar);
		}

		const int NearbyAmmoRadius = 6;
		const int NearbyAmmoRadiusSq = NearbyAmmoRadius * NearbyAmmoRadius;

		static Dictionary<Thing, List<Thing>> AssignShellsToNearestMortar(Map map, List<Thing> mortars)
		{
			var byMortar = new Dictionary<Thing, List<Thing>>();
			if (map?.listerThings == null || mortars == null || mortars.Count == 0)
				return byMortar;

			for (int i = 0; i < mortars.Count; i++)
				byMortar[mortars[i]] = new List<Thing>();

			List<Thing> items = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableAlways);
			for (int i = 0; i < items.Count; i++)
			{
				Thing shell = items[i];
				if (!IsMortarShellItem(shell)) continue;

				Thing nearest = null;
				int best = int.MaxValue;
				for (int m = 0; m < mortars.Count; m++)
				{
					Thing mortar = mortars[m];
					if (mortar == null || mortar.Destroyed) continue;
					int d = shell.Position.DistanceToSquared(mortar.Position);
					if (d > NearbyAmmoRadiusSq) continue;
					if (d >= best) continue;
					best = d;
					nearest = mortar;
				}
				if (nearest == null) continue;
				byMortar[nearest].Add(shell);
			}
			return byMortar;
		}

		static void DestroyDefenseBuildingAndOwnedAmmo(Thing building, Dictionary<Thing, List<Thing>> shellsByMortar)
		{
			if (building == null || building.Destroyed) return;

			if (IsMortarLike(building) && shellsByMortar != null
			    && shellsByMortar.TryGetValue(building, out List<Thing> owned))
			{
				for (int i = 0; i < owned.Count; i++)
				{
					try
					{
						if (owned[i] != null && !owned[i].Destroyed)
							owned[i].Destroy(DestroyMode.Vanish);
					}
					catch { /* ignore */ }
				}
			}

			try { building.Destroy(DestroyMode.KillFinalize); }
			catch { /* ignore */ }
		}

		static bool IsMortarLike(Thing t)
		{
			if (t?.def == null) return false;
			if (t.def == ThingDefOf.Turret_Mortar) return true;
			string name = t.def.defName ?? "";
			return name.IndexOf("Mortar", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static bool IsMortarShellItem(Thing t)
		{
			if (t == null || t.Destroyed || t.def == null) return false;
			if (t.def.category != ThingCategory.Item) return false;
			if (t.def.IsShell) return true;
			ThingCategoryDef cat = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("MortarShells");
			return cat != null && t.def.IsWithinCategory(cat);
		}

		static void PurgeOrphanMortarShells(Map map, Faction fac)
		{
			if (map?.listerThings == null) return;

			var mortarCells = new List<IntVec3>();
			List<Thing> buildings = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
			for (int i = 0; i < buildings.Count; i++)
			{
				Thing t = buildings[i];
				if (t == null || t.Destroyed || !t.Spawned) continue;
				if (!IsMortarLike(t)) continue;
				if (fac != null && t.Faction != fac) continue;
				mortarCells.Add(t.Position);
			}

			var doomed = new List<Thing>();
			List<Thing> items = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableAlways);
			for (int i = 0; i < items.Count; i++)
			{
				Thing t = items[i];
				if (!IsMortarShellItem(t)) continue;
				bool nearMortar = false;
				for (int j = 0; j < mortarCells.Count; j++)
				{
					if (t.Position.DistanceToSquared(mortarCells[j]) <= NearbyAmmoRadiusSq)
					{
						nearMortar = true;
						break;
					}
				}
				if (!nearMortar)
					doomed.Add(t);
			}

			for (int i = 0; i < doomed.Count; i++)
			{
				try
				{
					if (!doomed[i].Destroyed)
						doomed[i].Destroy(DestroyMode.Vanish);
				}
				catch { /* ignore */ }
			}
		}
	}
}
