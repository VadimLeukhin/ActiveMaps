using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>One anchored structure state from a strike map at End.</summary>
	public class StructureSketchEntry : IExposable
	{
		public int dx;
		public int dz;
		public string defName;
		public byte rot;
		public int hitPoints;
		/// <summary>Building was destroyed during the strike — remove on regen.</summary>
		public bool destroyVoid;

		public void ExposeData()
		{
			Scribe_Values.Look(ref dx, "dx");
			Scribe_Values.Look(ref dz, "dz");
			Scribe_Values.Look(ref defName, "def");
			Scribe_Values.Look(ref rot, "rot");
			Scribe_Values.Look(ref hitPoints, "hp");
			Scribe_Values.Look(ref destroyVoid, "void");
		}
	}

	public class StructureSketchDestroyRecord : IExposable
	{
		public IntVec3 cell;
		public string defName;

		public StructureSketchDestroyRecord() { }

		public StructureSketchDestroyRecord(IntVec3 cell, string defName)
		{
			this.cell = cell;
			this.defName = defName;
		}

		public void ExposeData()
		{
			Scribe_Values.Look(ref cell, "cell");
			Scribe_Values.Look(ref defName, "def");
		}
	}

	public static class StructureSketchUtility
	{
		const int MatchRadius = 2;

		public static bool IsStructureSketchTarget(Thing t, Faction fac)
		{
			if (t == null || t.Destroyed || !t.Spawned) return false;
			if (!(t is Building)) return false;
			if (FloorEntranceUtility.IsFloorEntrance(t)) return false;
			if (StrikeSalvageUtility.IsEnemyDefenseLedgerBuilding(t, fac)) return false;
			if (fac != null && t.Faction != fac) return false;

			ThingDef def = t.def;
			if (def?.building == null) return false;
			if (def.building.isNaturalRock) return false;
			if (t is Building_Door) return true;
			if (def.IsWall) return true;
			if (def.defName == "Column") return true;
			if (IsSandbagOrBarricade(def)) return true;

			string name = def.defName ?? "";
			if (name.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (name.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}

		public static bool IsSandbagOrBarricade(ThingDef def)
		{
			if (def == null) return false;
			if (def == ThingDefOf.Sandbags || def == ThingDefOf.Barricade) return true;
			string name = def.defName ?? "";
			if (name.IndexOf("Sandbag", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (name.IndexOf("Barricade", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}

		public static List<StructureSketchEntry> Capture(
			Map map,
			Faction fac,
			IEnumerable<StructureSketchDestroyRecord> destroyedDuringStrike)
		{
			var sketch = new List<StructureSketchEntry>();
			if (map == null || fac == null) return sketch;

			var anchorCells = new List<IntVec3>();
			var damaged = new List<Building>();

			List<Thing> buildings = map.listerThings?.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
			if (buildings != null)
			{
				for (int i = 0; i < buildings.Count; i++)
				{
					Thing t = buildings[i];
					if (!IsStructureSketchTarget(t, fac)) continue;
					anchorCells.Add(t.Position);
					if (t.HitPoints < t.MaxHitPoints)
						damaged.Add((Building)t);
				}
			}

			var voids = new List<StructureSketchDestroyRecord>();
			if (destroyedDuringStrike != null)
			{
				foreach (StructureSketchDestroyRecord rec in destroyedDuringStrike)
				{
					if (rec == null || !rec.cell.InBounds(map)) continue;
					if (rec.defName.NullOrEmpty()) continue;
					voids.Add(rec);
					anchorCells.Add(rec.cell);
				}
			}

			if (!TryComputeAnchor(anchorCells, out IntVec3 anchor))
				return sketch;

			for (int i = 0; i < damaged.Count; i++)
			{
				Building b = damaged[i];
				if (b == null || b.Destroyed) continue;
				string defName = b.def?.defName;
				if (defName.NullOrEmpty()) continue;
				sketch.Add(new StructureSketchEntry
				{
					dx = b.Position.x - anchor.x,
					dz = b.Position.z - anchor.z,
					defName = defName,
					rot = (byte)b.Rotation.AsInt,
					hitPoints = b.HitPoints,
					destroyVoid = false
				});
			}

			for (int i = 0; i < voids.Count; i++)
			{
				StructureSketchDestroyRecord rec = voids[i];
				sketch.Add(new StructureSketchEntry
				{
					dx = rec.cell.x - anchor.x,
					dz = rec.cell.z - anchor.z,
					defName = rec.defName,
					rot = 0,
					hitPoints = 0,
					destroyVoid = true
				});
			}

			return sketch;
		}

		public static void Apply(Map map, Faction fac, List<StructureSketchEntry> sketch)
		{
			if (map == null || fac == null || sketch == null || sketch.Count == 0) return;
			if (!TryFindAnchorFromBuildings(map, fac, out IntVec3 anchor))
				return;

			for (int i = 0; i < sketch.Count; i++)
				ApplyEntry(map, fac, anchor, sketch[i]);
		}

		static void ApplyEntry(Map map, Faction fac, IntVec3 anchor, StructureSketchEntry e)
		{
			if (e == null || e.defName.NullOrEmpty()) return;
			IntVec3 target = new IntVec3(anchor.x + e.dx, 0, anchor.z + e.dz);
			if (!target.InBounds(map)) return;

			if (e.destroyVoid)
			{
				Building doomed = FindMatchingStructure(map, target, e.defName, fac, rot: Rot4.Invalid, requireRot: false, radius: MatchRadius);
				if (doomed != null && !doomed.Destroyed)
				{
					try { doomed.Destroy(DestroyMode.Vanish); }
					catch (Exception ex) { Log.Warning($"[CrystallizeActiveMaps] structure void: {ex.Message}"); }
				}
				return;
			}

			Rot4 rot = new Rot4(e.rot);
			Building b = FindMatchingStructure(map, target, e.defName, fac, rot, requireRot: true, radius: 0);
			if (b == null)
				b = FindMatchingStructure(map, target, e.defName, fac, rot, requireRot: false, radius: MatchRadius);
			if (b == null || b.Destroyed) return;

			int hp = Math.Max(1, Math.Min(e.hitPoints, b.MaxHitPoints));
			try { b.HitPoints = hp; }
			catch (Exception ex) { Log.Warning($"[CrystallizeActiveMaps] structure hp: {ex.Message}"); }
		}

		static Building FindMatchingStructure(
			Map map,
			IntVec3 center,
			string defName,
			Faction fac,
			Rot4 rot,
			bool requireRot,
			int radius)
		{
			if (radius <= 0)
				return TryMatchAt(map, center, defName, fac, rot, requireRot);

			int bestDist = int.MaxValue;
			Building best = null;
			foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, radius, useCenter: true))
			{
				if (!cell.InBounds(map)) continue;
				int dist = cell.DistanceToSquared(center);
				if (dist > bestDist) continue;
				Building hit = TryMatchAt(map, cell, defName, fac, rot, requireRot);
				if (hit == null) continue;
				bestDist = dist;
				best = hit;
			}
			return best;
		}

		static Building TryMatchAt(
			Map map,
			IntVec3 cell,
			string defName,
			Faction fac,
			Rot4 rot,
			bool requireRot)
		{
			List<Thing> things = map.thingGrid.ThingsListAt(cell);
			for (int i = 0; i < things.Count; i++)
			{
				Thing t = things[i];
				if (!IsStructureSketchTarget(t, fac)) continue;
				if (t.def?.defName != defName) continue;
				if (requireRot && t.Rotation != rot) continue;
				return t as Building;
			}
			return null;
		}

		static bool TryFindAnchorFromBuildings(Map map, Faction fac, out IntVec3 anchor)
		{
			var cells = new List<IntVec3>();
			List<Thing> buildings = map.listerThings?.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
			if (buildings != null)
			{
				for (int i = 0; i < buildings.Count; i++)
				{
					Thing t = buildings[i];
					if (IsStructureSketchTarget(t, fac))
						cells.Add(t.Position);
				}
			}
			return TryComputeAnchor(cells, out anchor);
		}

		static bool TryComputeAnchor(List<IntVec3> cells, out IntVec3 anchor)
		{
			anchor = IntVec3.Invalid;
			if (cells == null || cells.Count == 0) return false;

			CellRect rect = CellRect.SingleCell(cells[0]);
			for (int i = 1; i < cells.Count; i++)
				rect = rect.Encapsulate(cells[i]);
			anchor = rect.CenterCell;
			return anchor.IsValid;
		}
	}
}
