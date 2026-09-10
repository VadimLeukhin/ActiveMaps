using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Player outpost: Mode1 stash / Mode2 sim / Mode3 only via PromoteForRaid.
	/// Player cannot Enter by caravan; ledger-first for buildings/ammo.
	/// </summary>
	public class AbstractOutpost : MapParent, IThingHolder
	{
		public const string PinRaid = "activemaps.raid";
		public const string DefName = "Crystallize_AbstractOutpost";

		PresenceMode mode = PresenceMode.Stash;
		List<Pawn> occupants = new List<Pawn>();
		ThingOwner<Thing> items;
		BuildingLedger ledger = new BuildingLedger();
		int fireCooldownTicks;
		bool raidActive;
		Faction raidFaction;
		bool raidVictoryHandled;
		int raidClearTicks;
		bool raidSawHostile;

		/// <summary>Optional ammo key in ledger (e.g. Shell_HighExplosive).</summary>
		public string PrimaryAmmoDefName;

		public PresenceMode Mode => mode;
		public BuildingLedger Ledger => ledger;
		public List<Pawn> Occupants => occupants;
		public bool RaidActive => raidActive;

		protected override bool UseGenericEnterMapFloatMenuOption => false;

		public new ThingOwner GetDirectlyHeldThings() => items;

		public AbstractOutpost()
		{
			items = new ThingOwner<Thing>(this);
		}

		public override void GetChildHolders(List<IThingHolder> outChildren)
		{
			ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
			if (HasMap)
				outChildren.Add(Map);
		}

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Values.Look(ref mode, "presenceMode", PresenceMode.Stash);
			// Occupants live in WorldPawns (PassToWorld on form/fold) — never Deep (duplicate loadIDs / MVCF spam).
			Scribe_Collections.Look(ref occupants, "occupants", LookMode.Reference);
			Scribe_Deep.Look(ref items, "items", this);
			Scribe_Deep.Look(ref ledger, "ledger");
			Scribe_Values.Look(ref fireCooldownTicks, "fireCooldownTicks", 0);
			Scribe_Values.Look(ref raidActive, "raidActive", false);
			Scribe_Values.Look(ref raidVictoryHandled, "raidVictoryHandled", false);
			Scribe_Values.Look(ref raidClearTicks, "raidClearTicks", 0);
			Scribe_Values.Look(ref raidSawHostile, "raidSawHostile", false);
			Scribe_Values.Look(ref PrimaryAmmoDefName, "primaryAmmoDefName");
			Scribe_References.Look(ref raidFaction, "raidFaction");
			if (occupants == null) occupants = new List<Pawn>();
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
				occupants.RemoveAll(p => p == null || p.Destroyed);
			if (items == null) items = new ThingOwner<Thing>(this);
			if (ledger == null) ledger = new BuildingLedger();
		}

		protected override void TickInterval(int delta)
		{
			base.TickInterval(delta);
			if (mode == PresenceMode.Simulate && fireCooldownTicks > 0)
			{
				fireCooldownTicks = Mathf.Max(0, fireCooldownTicks - delta);
				if (fireCooldownTicks <= 0)
					NotifyFireCooldownReady();
			}

			if (raidActive && HasMap)
				CheckRaidVictoryFromMap(Map, ref raidVictoryHandled);
		}

		/// <summary>Called from outpost tick and MapComponent_OutpostRaid.</summary>
		public void CheckRaidVictoryFromMap(Map map, ref bool handledFlag)
		{
			if (!raidActive || map == null || handledFlag || raidVictoryHandled) return;
			if (OutpostRaidCombat.HasActiveHostileThreat(map))
			{
				raidSawHostile = true;
				raidClearTicks = 0;
				return;
			}

			// Do not fold before the raid incident has spawned anyone.
			if (!raidSawHostile) return;

			raidClearTicks++;
			if (raidClearTicks < 2) return;

			handledFlag = true;
			raidVictoryHandled = true;
			NotifyRaidVictoryAndFold(map);
		}

		void NotifyRaidVictoryAndFold(Map map)
		{
			bool playersAlive = OutpostRaidCombat.HasLivingPlayerPawn(map);
			Find.LetterStack.ReceiveLetter(
				"Crystallize_AM_RaidWonLabel".Translate(LabelCap),
				playersAlive
					? "Crystallize_AM_RaidWonText".Translate(LabelCap)
					: "Crystallize_AM_RaidWonWipeText".Translate(LabelCap),
				playersAlive ? LetterDefOf.PositiveEvent : LetterDefOf.NeutralEvent,
				this);

			Messages.Message(
				playersAlive
					? "Crystallize_AM_RaidWonMessage".Translate(LabelCap)
					: "Crystallize_AM_RaidWonWipeMessage".Translate(LabelCap),
				this,
				playersAlive ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NeutralEvent,
				false);

			// Mode3 is raid-only: fold back to stash (scoops loot / stabilizes).
			DemoteToStash();
		}

		public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
		{
			alsoRemoveWorldObject = false;
			if (Map == null) return false;
			if (raidActive) return false;
			if (ActiveMaps.IsPinned(Map)) return false;
			// After raid cleared, keep map until Demote is called explicitly.
			return false;
		}

		public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
		{
			foreach (var opt in base.GetFloatMenuOptions(caravan))
				yield return opt;
			// Enter suppressed via UseGenericEnterMapFloatMenuOption = false
		}

		public override void DrawExtraSelectionOverlays()
		{
			base.DrawExtraSelectionOverlays();
			if (Tile.Valid)
				GenDraw.DrawWorldRadiusRing(Tile, OutpostStrikeZone.MaxWorldRangeTiles);
		}

		public override IEnumerable<Gizmo> GetGizmos()
		{
			foreach (var g in base.GetGizmos())
				yield return g;

			if (Prefs.DevMode)
			{
				yield return new Command_Action
				{
					defaultLabel = "DEV: Demote to stash",
					action = () =>
					{
						if (HasMap) DemoteToStash();
					}
				};
				yield return new Command_Action
				{
					defaultLabel = "DEV: Simulate raid letter",
					action = () => OutpostRaidUtility.SendRaidLetter(this)
				};
				yield return new Command_Action
				{
					defaultLabel = "DEV: +10 HE shells ledger",
					action = () =>
					{
						PrimaryAmmoDefName = PrimaryAmmoDefName ?? "Shell_HighExplosive";
						ledger.Add(PrimaryAmmoDefName, 10);
					}
				};
			}

			if (!HasMap)
			{
				yield return new Command_Action
				{
					defaultLabel = "Crystallize_AM_DisbandOutpost".Translate(),
					defaultDesc = "Crystallize_AM_DisbandOutpostDesc".Translate(),
					action = () => DisbandToCaravan()
				};
			}
			else if (raidActive)
			{
				yield return new Command_Action
				{
					defaultLabel = "Crystallize_AM_EndRaidDemote".Translate(),
					defaultDesc = "Crystallize_AM_EndRaidDemoteDesc".Translate(),
					action = () => DemoteToStash()
				};
			}

			if (OutpostStrikeZone.Active && OutpostStrikeZone.OutpostId == ID)
			{
				yield return new Command_Action
				{
					defaultLabel = "Crystallize_AM_ChangeTarget".Translate(),
					defaultDesc = "Crystallize_AM_ChangeTargetDesc".Translate(),
					icon = FireMissionUi.TargetIcon,
					action = OutpostStrikeZone.BeginLocalRetarget
				};
				yield return new Command_Action
				{
					defaultLabel = "Crystallize_AM_StopFire".Translate(),
					defaultDesc = "Crystallize_AM_StopFireDesc".Translate(),
					icon = FireMissionUi.StopFireIcon,
					action = OutpostStrikeZone.StopFire
				};
				yield return new Command_Action
				{
					defaultLabel = "Crystallize_AM_EndStrike".Translate(),
					defaultDesc = "Crystallize_AM_EndStrikeDesc".Translate(),
					icon = TexCommand.ClearPrioritizedWork,
					action = () => OutpostStrikeZone.EndMission(jumpHome: true)
				};
			}
		}

		public override string GetInspectString()
		{
			var sb = new StringBuilder(base.GetInspectString());
			if (sb.Length > 0) sb.AppendLine();
			sb.AppendLine("Crystallize_AM_PresenceMode".Translate(mode.ToString()));
			sb.AppendLine("Crystallize_AM_Occupants".Translate(occupants.Count));
			sb.Append("Crystallize_AM_ItemStacks".Translate(items.Count));
			ThingDef ammoDef = DefDatabase<ThingDef>.GetNamedSilentFail(PrimaryAmmoDefName ?? "Shell_HighExplosive");
			if (ammoDef != null)
			{
				sb.AppendLine();
				sb.Append("Crystallize_AM_AmmoAvailable".Translate(CountAmmoAvailable(ammoDef), ammoDef.label));
			}
			if (fireCooldownTicks > 0)
			{
				sb.AppendLine();
				sb.Append("Crystallize_AM_FireCooldown".Translate(fireCooldownTicks.ToStringSecondsFromTicks()));
			}
			if (raidActive)
			{
				sb.AppendLine();
				sb.Append("Crystallize_AM_RaidActive".Translate());
			}
			string mission = OutpostStrikeZone.Active && OutpostStrikeZone.OutpostId == ID
				? OutpostStrikeZone.InspectExtra()
				: null;
			if (!mission.NullOrEmpty())
			{
				sb.AppendLine();
				sb.Append(mission);
			}
			return sb.ToString().TrimEnd();
		}

		public static AbstractOutpost CreateFromCaravan(Caravan caravan, PlanetTile tile)
		{
			if (caravan == null || !tile.Valid) return null;
			WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamedSilentFail(DefName);
			if (def == null)
			{
				Log.Error("[CrystallizeActiveMaps] Missing WorldObjectDef Crystallize_AbstractOutpost");
				return null;
			}

			var site = (AbstractOutpost)WorldObjectMaker.MakeWorldObject(def);
			site.Tile = tile;
			site.SetFaction(Faction.OfPlayer);
			Find.WorldObjects.Add(site);

			List<Pawn> pawns = caravan.PawnsListForReading.ToList();
			foreach (Pawn p in pawns)
			{
				caravan.RemovePawn(p);
				if (!p.IsWorldPawn())
					Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.Decide);
				site.occupants.Add(p);
			}

			// Inventory left on caravan beasts already moved with pawns; remaining caravan things:
			List<Thing> leftover = CaravanInventoryUtility.AllInventoryItems(caravan).ToList();
			foreach (Thing t in leftover)
			{
				t.holdingOwner?.Remove(t);
				site.items.TryAddOrTransfer(t, canMergeWithExistingStacks: true);
			}

			caravan.Destroy();
			site.mode = PresenceMode.Stash;
			Messages.Message("Crystallize_AM_OutpostFormed".Translate(site.LabelCap), site, MessageTypeDefOf.PositiveEvent);
			return site;
		}

		public Caravan DisbandToCaravan()
		{
			if (HasMap)
			{
				Messages.Message("Crystallize_AM_CannotDisbandWhileMapped".Translate(), this, MessageTypeDefOf.RejectInput, false);
				return null;
			}
			if (occupants.Count == 0)
			{
				Messages.Message("Crystallize_AM_OutpostEmpty".Translate(), this, MessageTypeDefOf.RejectInput, false);
				return null;
			}

			// SeedLedger / fire ammo live in abstract ledger — fold portable stacks into items first.
			MaterializePortableLedgerIntoItems();

			PlanetTile tile = Tile;
			List<Pawn> pawns = occupants.ToList();
			foreach (Pawn p in pawns)
				OutpostHealthStabilize.StabilizeForStash(p);
			occupants.Clear();
			List<Thing> stuff = items.ToList();
			items.Clear();

			Caravan caravan = CaravanMaker.MakeCaravan(pawns, Faction.OfPlayer, tile, addToWorldPawnsIfNotAlready: true);
			foreach (Thing t in stuff)
			{
				if (t is Pawn) continue;
				CaravanInventoryUtility.GiveThing(caravan, t);
			}

			Destroy();
			Messages.Message("Crystallize_AM_OutpostDisbanded".Translate(), caravan, MessageTypeDefOf.NeutralEvent);
			return caravan;
		}

		/// <summary>
		/// Turn ledger item counts (shells etc.) into real Thing stacks in <see cref="items"/>.
		/// Buildings stay abstract for Promote/sketch; only ThingCategory.Item is materialized.
		/// </summary>
		public void MaterializePortableLedgerIntoItems()
		{
			foreach (KeyValuePair<string, int> kv in ledger.All.ToList())
			{
				if (kv.Key.NullOrEmpty() || kv.Value <= 0) continue;
				ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key);
				if (def == null || def.category != ThingCategory.Item) continue;
				if (def.thingClass != null && typeof(Building).IsAssignableFrom(def.thingClass))
					continue;

				int left = kv.Value;
				int stackLimit = def.stackLimit > 0 ? def.stackLimit : left;
				while (left > 0)
				{
					int n = Mathf.Min(stackLimit, left);
					Thing t = ThingMaker.MakeThing(def);
					t.stackCount = n;
					items.TryAddOrTransfer(t, canMergeWithExistingStacks: true);
					left -= n;
				}
				ledger.Set(kv.Key, 0);
			}
		}

		/// <summary>Only legal way for player outpost to enter Mode3.</summary>
		public Map PromoteForRaid(Faction attacker = null)
		{
			if (HasMap)
			{
				raidActive = true;
				raidFaction = attacker;
				raidVictoryHandled = false;
				raidClearTicks = 0;
				raidSawHostile = false;
				mode = PresenceMode.Map;
				ActiveMaps.Pin(Map, PinRaid, ActiveMapReason.Other);
				MapComponent_OutpostRaid.Ensure(Map, this);
				return Map;
			}

			raidActive = true;
			raidFaction = attacker;
			raidVictoryHandled = false;
			raidClearTicks = 0;
			raidSawHostile = false;
			Map map = GetOrGenerateMapUtility.GetOrGenerateMap(Tile, def);
			mode = PresenceMode.Map;
			ActiveMaps.Pin(map, PinRaid, ActiveMapReason.Other);

			OutpostRaidUtility.NotifySketchProviders(this, map);
			SpawnOccupantsOntoMap(map);
			SpawnStashItemsOntoMap(map);
			MapComponent_OutpostRaid.Ensure(map, this);

			ActiveMaps.JumpToMap(map);
			return map;
		}

		public void DemoteToStash()
		{
			if (!HasMap)
			{
				mode = PresenceMode.Stash;
				raidActive = false;
				return;
			}

			Map map = Map;
			bool hostilesRemain = map.mapPawns.AllPawnsSpawned.Any(
				p => p.HostileTo(Faction.OfPlayer) && GenHostility.IsActiveThreatToPlayer(p));
			bool livingPlayers = OutpostRaidCombat.HasLivingPlayerPawn(map);

			// Mid-combat fold blocked — unless wipe: no living player pawns left (abandon map).
			if (hostilesRemain && livingPlayers)
			{
				Messages.Message("Crystallize_AM_CannotDemoteInCombat".Translate(), this, MessageTypeDefOf.RejectInput, false);
				return;
			}

			bool abandonWipe = hostilesRemain && !livingPlayers;
			FoldMapIntoStash(map);
			ActiveMaps.Unpin(map, PinRaid);
			raidActive = false;
			raidVictoryHandled = false;
			raidClearTicks = 0;
			raidSawHostile = false;
			mode = PresenceMode.Stash;

			StrikeSalvageUtility.EnsureUiNotOnMap(map);
			Current.Game.DeinitAndRemoveMap(map, notifyPlayer: false);
			if (abandonWipe)
				Messages.Message("Crystallize_AM_RaidAbandonedWipe".Translate(), this, MessageTypeDefOf.NeutralEvent, false);
		}

		void FoldMapIntoStash(Map map)
		{
			occupants.RemoveAll(p => p == null || p.Destroyed);
			var toFold = new List<Pawn>();
			foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
			{
				if (p.Faction != Faction.OfPlayer) continue;
				if (!StashFoldHooks.CanFoldPawn(p, this)) continue;
				toFold.Add(p);
			}

			foreach (Pawn p in toFold)
			{
				OutpostHealthStabilize.StabilizeForStash(p);
				p.jobs?.StopAll();
				p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ExitedMap);
				p.DeSpawn(DestroyMode.Vanish);
				if (!p.IsWorldPawn())
					Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.KeepForever);
				if (!occupants.Contains(p))
					occupants.Add(p);
			}

			var scoop = new List<Thing>();
			foreach (Thing t in map.listerThings.AllThings.ToList())
			{
				if (t == null || t.Destroyed || t is Pawn) continue;
				if (t.def.category != ThingCategory.Item) continue;
				if (!ShouldScoopItem(t)) continue;
				scoop.Add(t);
			}
			StashFoldHooks.CollectExtraThings(this, map, scoop);

			foreach (Thing t in scoop.Distinct())
			{
				if (t.Destroyed || !t.Spawned) continue;
				t.DeSpawn(DestroyMode.Vanish);
				items.TryAddOrTransfer(t, canMergeWithExistingStacks: true);
			}

			StashFoldHooks.AfterFold(this);
		}

		static bool ShouldScoopItem(Thing t)
		{
			if (t.Faction == Faction.OfPlayer) return true;
			if (t.Faction != null && t.Faction.HostileTo(Faction.OfPlayer)) return false;
			// Unfactioned ground loot (dropped weapons, etc.)
			return t.def.category == ThingCategory.Item;
		}

		void SpawnOccupantsOntoMap(Map map)
		{
			IntVec3 center = map.Center;
			foreach (Pawn p in occupants.ToList())
			{
				if (p == null || p.Destroyed) continue;
				IntVec3 cell = CellFinder.RandomSpawnCellForPawnNear(center, map, 8);
				GenSpawn.Spawn(p, cell, map, WipeMode.Vanish);
			}
			occupants.Clear();
		}

		void SpawnStashItemsOntoMap(Map map)
		{
			IntVec3 cell = map.Center;
			foreach (Thing t in items.ToList())
			{
				items.Remove(t);
				GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Near);
			}
		}

		/// <summary>Start Fire Mission: load target map, pin, jump, local aim (railgun-style).</summary>
		public bool BeginStrikeMission(PlanetTile targetTile, ThingDef shellDef)
		{
			if (!OutpostStrikeZone.TryBeginFromWorld(this, targetTile, shellDef, out string error))
			{
				Messages.Message(error ?? "Crystallize_AM_MapFailed".Translate(), this, MessageTypeDefOf.RejectInput, false);
				return false;
			}
			return true;
		}

		public void NotifyStrikeMissionStarted()
		{
			mode = PresenceMode.Simulate;
		}

		public void NotifyStrikeMissionEnded()
		{
			if (!raidActive && !HasMap)
				mode = PresenceMode.Stash;
			fireCooldownTicks = 0;
		}

		public int CountAmmoAvailable(ThingDef shellDef)
		{
			if (shellDef == null) return 0;
			int n = ledger.Get(shellDef.defName);
			foreach (Thing t in items)
			{
				if (t != null && t.def == shellDef)
					n += t.stackCount;
			}
			foreach (Pawn p in occupants)
			{
				if (p?.inventory?.innerContainer == null) continue;
				foreach (Thing t in p.inventory.innerContainer)
				{
					if (t != null && t.def == shellDef)
						n += t.stackCount;
				}
			}
			return n;
		}

		/// <summary>Physical stash / pawn inventory first, then abstract ledger.</summary>
		public bool TryConsumeAmmo(ThingDef shellDef, int amount)
		{
			if (shellDef == null || amount <= 0) return amount <= 0;
			if (CountAmmoAvailable(shellDef) < amount) return false;

			int need = amount;
			need -= TakeFromOwner(items, shellDef, need);
			if (need > 0)
			{
				foreach (Pawn p in occupants)
				{
					if (need <= 0) break;
					if (p?.inventory?.innerContainer == null) continue;
					need -= TakeFromOwner(p.inventory.innerContainer, shellDef, need);
				}
			}
			if (need > 0)
			{
				if (!ledger.TryConsume(shellDef.defName, need))
					return false;
			}
			return true;
		}

		static int TakeFromOwner(ThingOwner owner, ThingDef def, int amount)
		{
			if (owner == null || amount <= 0) return 0;
			int taken = 0;
			for (int i = owner.Count - 1; i >= 0 && taken < amount; i--)
			{
				Thing t = owner[i];
				if (t == null || t.def != def) continue;
				int grab = Mathf.Min(t.stackCount, amount - taken);
				Thing got = owner.Take(t, grab);
				got?.Destroy(DestroyMode.Vanish);
				taken += grab;
			}
			return taken;
		}

		public void NotifyFireCooldownReady()
		{
			if (!raidActive && !HasMap)
				mode = PresenceMode.Stash;
		}
	}
}
