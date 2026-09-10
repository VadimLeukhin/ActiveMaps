using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace Crystallize.ActiveMaps
{
	/// <summary>Phase 1 capture pipeline — Habitation flags = former CaptureAlive body.</summary>
	public static class VgCapturePipeline
	{
		public static void Run(Map map, MapParent parent, VgProfile profile)
		{
			var wc = WorldComponent_StrikeAftermath.Get();
			if (wc == null || map == null || parent == null) return;
			if (!profile.Has(VgFlags.FoldDefenders) && !profile.Has(VgFlags.DefenseLedger)
			    && !profile.Has(VgFlags.StructureSketch))
				return;

			// Multi-floor: never Deinit surface — portals + ChildPocketMaps must stay linked.
			if (FloorEntranceUtility.MapHasFloorEntrance(map)
			    || FloorEntranceUtility.MapHasLinkedPocketMaps(map))
			{
				RunKeepLinkedFloors(map, parent, profile);
				return;
			}

			using (SettlementDefeatSuppress.Scope())
			{
				Faction fac = parent.Faction;
				StrikeAftermathEntry entry = wc.GetOrCreate(parent, StrikeAftermathMode.VirtualGarrison);
				VgProfileResolver.StampEntry(entry, profile);
				entry.expireTick = -1;
				entry.ledgerApplied = false;
				entry.keepLinkedFloors = false;
				if (profile.Id == VgProfileIds.QuestVgLight)
					entry.shellsLanded = true;
				entry.garrison.Clear();

				if (profile.Has(VgFlags.DefenseLedger))
					entry.survivingDefenseByDef = StrikeSalvageUtility.CountEnemyDefenseByDef(map, fac);
				else
					entry.survivingDefenseByDef = new Dictionary<string, int>();

				if (profile.Has(VgFlags.StructureSketch))
				{
					MapComponent_VirtualGarrisonStructureTracker tracker =
						MapComponent_VirtualGarrisonStructureTracker.Ensure(map, fac);
					entry.structureSketch = StructureSketchUtility.Capture(map, fac, tracker?.DestroyedRecords);
				}
				else
					entry.structureSketch = new List<StructureSketchEntry>();

				// Sticky destroyed quest things (hostage/terminal) before evacuate — regen cull.
				if (parent is Site captureSite)
					QuestDestroyedLedger.CaptureAll(map, captureSite, entry);

				// QuestVgLight: pull CompHackable / quest-tagged things off-map before Unload
				// so Despawn during Deinit does not fail the quest; restore on Apply.
				if (profile.Has(VgFlags.EvacuateQuestSignificant) && parent is Site site)
					QuestSignificantSet.EvacuateForQuestVgLight(map, site, entry.loot);

				if (profile.Has(VgFlags.FoldDefenders))
					FoldFactionDefenders(map, fac, entry);

				StrikeSalvageUtility.UnloadMap(map);
				string msgKey = profile.Id == VgProfileIds.QuestVgLight
					? "Crystallize_AM_QuestVgLightSaved"
					: "Crystallize_AM_VirtualGarrisonSaved";
				Messages.Message(
					msgKey.Translate(
						entry.garrison.Count,
						StrikeSalvageUtility.TotalDefenseCount(entry.survivingDefenseByDef)),
					parent, MessageTypeDefOf.NeutralEvent, false);
			}
		}

		/// <summary>
		/// Keep surface map loaded: damage/kills already on the map; entrances stay enterable;
		/// already-open pocket maps are not destroyed with parent unload.
		/// </summary>
		public static void RunKeepLinkedFloors(Map map, MapParent parent, VgProfile profile)
		{
			var wc = WorldComponent_StrikeAftermath.Get();
			if (wc == null || map == null || parent == null) return;

			Faction fac = parent.Faction;
			StrikeAftermathEntry entry = wc.GetOrCreate(parent, StrikeAftermathMode.VirtualGarrison);
			VgProfileResolver.StampEntry(entry, profile);
			entry.expireTick = -1;
			entry.keepLinkedFloors = true;
			entry.holdMapPinned = true;
			// Surface already matches strike outcome — do not cull/regen on next Generate.
			entry.ledgerApplied = true;
			entry.garrison.Clear();
			entry.structureSketch = new List<StructureSketchEntry>();
			entry.survivingDefenseByDef = profile.Has(VgFlags.DefenseLedger)
				? StrikeSalvageUtility.CountEnemyDefenseByDef(map, fac)
				: new Dictionary<string, int>();

			ActiveMaps.Pin(map, WorldComponent_StrikeAftermath.PinLinkedFloors, ActiveMapReason.LootWindow);

			int defenders = 0;
			var spawned = map.mapPawns?.AllPawnsSpawned;
			if (spawned != null && fac != null)
			{
				for (int i = 0; i < spawned.Count; i++)
				{
					Pawn p = spawned[i];
					if (p != null && !p.Destroyed && p.Faction == fac)
						defenders++;
				}
			}

			Messages.Message(
				"Crystallize_AM_LinkedFloorsKept".Translate(
					defenders,
					StrikeSalvageUtility.TotalDefenseCount(entry.survivingDefenseByDef)),
				parent, MessageTypeDefOf.NeutralEvent, false);
		}

		static void FoldFactionDefenders(Map map, Faction fac, StrikeAftermathEntry entry)
		{
			List<Pawn> spawned = map.mapPawns?.AllPawnsSpawned?.ToList() ?? new List<Pawn>();
			for (int i = 0; i < spawned.Count; i++)
			{
				Pawn p = spawned[i];
				if (p == null || p.Destroyed || !p.Spawned) continue;
				if (p.Faction != fac) continue;
				// Belt-and-suspenders: quest hostages evacuate first; never fold them into garrison.
				if (QuestUtility.IsQuestLodger(p)) continue;
				if (p.questTags != null && p.questTags.Count > 0) continue;
				if (p.IsPrisoner) continue;
				if (p.RaceProps != null && p.RaceProps.Animal && p.Faction == null) continue;
				try
				{
					FoldDefenderIntoWorld(p);
					if (!entry.garrison.Contains(p))
						entry.garrison.Add(p);
				}
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] VG fold pawn: {e.Message}");
				}
			}
		}

		/// <summary>
		/// Store defender off-map. Do NOT Notify_PawnLost(ExitedMap) — that tells DefendBase
		/// lords to evacuate and restored pawns flee on the next map open.
		/// </summary>
		public static void FoldDefenderIntoWorld(Pawn p)
		{
			if (p == null) return;
			QuestSignificantSet.ScrubPawnBeforeWorldEvacuate(p);

			Lord lord = p.GetLord();
			if (lord != null)
				lord.RemovePawn(p);

			try { p.mindState?.Reset(clearInspiration: false, clearMentalState: true); } catch { /* ignore */ }

			if (p.Spawned)
				p.DeSpawn(DestroyMode.Vanish);
			if (!p.IsWorldPawn())
				Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.KeepForever);
		}
	}
}
