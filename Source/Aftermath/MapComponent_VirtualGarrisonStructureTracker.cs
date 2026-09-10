using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>Records faction shell buildings destroyed during a strike map session.</summary>
	public class MapComponent_VirtualGarrisonStructureTracker : MapComponent
	{
		Faction trackedFaction;
		List<StructureSketchDestroyRecord> destroyed = new List<StructureSketchDestroyRecord>();

		public MapComponent_VirtualGarrisonStructureTracker(Map map) : base(map) { }

		public static MapComponent_VirtualGarrisonStructureTracker Ensure(Map map, Faction fac)
		{
			if (map == null || fac == null) return null;
			MapComponent_VirtualGarrisonStructureTracker comp = map.GetComponent<MapComponent_VirtualGarrisonStructureTracker>();
			if (comp == null)
			{
				comp = new MapComponent_VirtualGarrisonStructureTracker(map);
				map.components.Add(comp);
			}
			comp.trackedFaction = fac;
			return comp;
		}

		public IReadOnlyList<StructureSketchDestroyRecord> DestroyedRecords => destroyed;

		public void RecordDestroyed(Building b)
		{
			if (b == null || b.Destroyed || map == null) return;
			if (trackedFaction == null || b.Faction != trackedFaction) return;
			if (!StructureSketchUtility.IsStructureSketchTarget(b, trackedFaction)) return;

			string defName = b.def?.defName;
			if (defName.NullOrEmpty()) return;

			IntVec3 cell = b.Position;
			for (int i = 0; i < destroyed.Count; i++)
			{
				if (destroyed[i] != null && destroyed[i].cell == cell && destroyed[i].defName == defName)
					return;
			}
			destroyed.Add(new StructureSketchDestroyRecord(cell, defName));
		}

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_References.Look(ref trackedFaction, "fac");
			Scribe_Collections.Look(ref destroyed, "destroyed", LookMode.Deep);
			if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				if (destroyed == null)
					destroyed = new List<StructureSketchDestroyRecord>();
				destroyed.RemoveAll(r => r == null || r.defName.NullOrEmpty());
			}
		}
	}
}
