using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	public static class StrikeSalvageUtility
	{
		/// <summary>
		/// Count enemy defense buildings by ThingDef.defName for VG cull.
		/// Includes: turrets/mortars, batteries, power generators, projectile shields, traps.
		/// Not included (wrong tool): conduits/switches (need sketch voids), coolers/furniture consumers.
		/// </summary>
		public static Dictionary<string, int> CountEnemyDefenseByDef(Map map, Faction faction)
		{
			var counts = new Dictionary<string, int>();
			if (map?.listerThings == null) return counts;
			List<Thing> list = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
			for (int i = 0; i < list.Count; i++)
			{
				Thing t = list[i];
				if (t == null || t.Destroyed || !t.Spawned) continue;
				if (!IsEnemyDefenseLedgerBuilding(t, faction)) continue;
				string key = t.def?.defName;
				if (key.NullOrEmpty()) continue;
				counts.TryGetValue(key, out int n);
				counts[key] = n + 1;
			}
			return counts;
		}

		public static int TotalDefenseCount(Dictionary<string, int> byDef)
		{
			if (byDef == null || byDef.Count == 0) return 0;
			int n = 0;
			foreach (KeyValuePair<string, int> kv in byDef)
				n += Math.Max(0, kv.Value);
			return n;
		}

		/// <summary>
		/// Discrete combat/power buildings — VG ledger capture/cull by defName.
		/// Conduits, switches, and power consumers stay out (count-cull breaks nets / wrong semantics).
		/// </summary>
		public static bool IsEnemyDefenseLedgerBuilding(Thing t, Faction faction)
		{
			return IsEnemyTurret(t, faction)
			       || IsEnemyBattery(t, faction)
			       || IsEnemyPowerGenerator(t, faction)
			       || IsEnemyShield(t, faction)
			       || IsEnemyTrap(t, faction);
		}

		static bool MatchesLedgerFaction(Thing t, Faction faction)
		{
			if (t == null) return false;
			if (faction != null) return t.Faction == faction;
			return t.Faction != null && t.Faction.HostileTo(Faction.OfPlayer);
		}

		public static bool IsEnemyTurret(Thing t, Faction faction)
		{
			if (t is Building_Turret)
				return MatchesLedgerFaction(t, faction);
			string name = t.def?.defName ?? "";
			if (name.IndexOf("Turret", StringComparison.OrdinalIgnoreCase) >= 0
			    || name.IndexOf("Mortar", StringComparison.OrdinalIgnoreCase) >= 0)
				return MatchesLedgerFaction(t, faction);
			return false;
		}

		public static bool IsEnemyBattery(Thing t, Faction faction)
		{
			if (t == null || t.Destroyed) return false;
			bool isBattery = t.TryGetComp<CompPowerBattery>() != null;
			if (!isBattery)
			{
				string name = t.def?.defName ?? "";
				if (name.IndexOf("Battery", StringComparison.OrdinalIgnoreCase) < 0)
					return false;
			}
			return MatchesLedgerFaction(t, faction);
		}

		public static bool IsEnemyPowerGenerator(Thing t, Faction faction)
		{
			if (t == null || t.Destroyed) return false;
			// Batteries and shields have their own ledger lanes.
			if (t.TryGetComp<CompPowerBattery>() != null) return false;
			if (t.TryGetComp<CompProjectileInterceptor>() != null) return false;

			bool isGenerator = t.TryGetComp<CompPowerPlant>() != null;
			if (!isGenerator)
			{
				CompPowerTrader trader = t.TryGetComp<CompPowerTrader>();
				if (trader != null && trader.PowerOutput > 0f)
					isGenerator = true;
			}
			if (!isGenerator)
			{
				string name = t.def?.defName ?? "";
				if (name.IndexOf("Battery", StringComparison.OrdinalIgnoreCase) >= 0) return false;
				if (name.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) >= 0) return false;
				if (name.IndexOf("Generator", StringComparison.OrdinalIgnoreCase) < 0
				    && name.IndexOf("Reactor", StringComparison.OrdinalIgnoreCase) < 0
				    && name.IndexOf("Turbine", StringComparison.OrdinalIgnoreCase) < 0)
					return false;
			}
			return MatchesLedgerFaction(t, faction);
		}

		/// <summary>Mech/mod projectile shields (CompProjectileInterceptor).</summary>
		public static bool IsEnemyShield(Thing t, Faction faction)
		{
			if (t == null || t.Destroyed) return false;
			if (t.TryGetComp<CompProjectileInterceptor>() != null)
				return MatchesLedgerFaction(t, faction);
			string name = t.def?.defName ?? "";
			if (name.IndexOf("ShieldGenerator", StringComparison.OrdinalIgnoreCase) >= 0
			    || name.IndexOf("ProjectileInterceptor", StringComparison.OrdinalIgnoreCase) >= 0)
				return MatchesLedgerFaction(t, faction);
			return false;
		}

		/// <summary>Spike / IED traps — regenerate on settlement reopen unless culled.</summary>
		public static bool IsEnemyTrap(Thing t, Faction faction)
		{
			if (t == null || t.Destroyed) return false;
			if (t is Building_Trap)
				return MatchesLedgerFaction(t, faction);
			if (t.def?.building != null && t.def.building.isTrap)
				return MatchesLedgerFaction(t, faction);
			string name = t.def?.defName ?? "";
			if (name.IndexOf("TrapIED", StringComparison.OrdinalIgnoreCase) >= 0
			    || name.StartsWith("Trap", StringComparison.OrdinalIgnoreCase))
				return MatchesLedgerFaction(t, faction);
			return false;
		}

		public static PlanetTile SalvageSiteTile(MapParent parent, StrikeAftermathEntry entry)
		{
			if (entry?.tile.Valid == true) return entry.tile;
			return parent?.Tile ?? PlanetTile.Invalid;
		}

		/// <summary>Salvage stash pickup — caravan must stand on the ruins tile (no remote transfer).</summary>
		public static bool CaravanCanTakeSalvageLoot(Caravan caravan, PlanetTile siteTile)
		{
			return caravan != null && caravan.Spawned && siteTile.Valid && caravan.Tile == siteTile;
		}

		public static void FoldLootIntoEntry(Map map, StrikeAftermathEntry entry, bool tryMinify)
		{
			if (map == null || entry?.loot == null) return;
			if (tryMinify)
				MinifyEverythingBridge.TryMinifyEligibleBuildings(map);

			var scoop = new List<Thing>();
			foreach (Thing t in map.listerThings.AllThings.ToList())
			{
				if (t == null || t.Destroyed || !t.Spawned) continue;
				if (t is Pawn) continue;
				// Items only — Corpse is ThingCategory.Item in vanilla, no separate branch.
				if (t.def.category == ThingCategory.Item)
					scoop.Add(t);
			}

			foreach (Thing t in scoop)
			{
				if (t.Destroyed || !t.Spawned) continue;
				try
				{
					t.DeSpawn(DestroyMode.Vanish);
					entry.loot.TryAddOrTransfer(t, canMergeWithExistingStacks: true);
				}
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] FoldLoot: {e.Message}");
				}
			}
		}

		public static void ApplySalvageDefeated(Map map, MapParent parent)
		{
			var wc = WorldComponent_StrikeAftermath.Get();
			if (wc == null || map == null || parent == null) return;

			Faction fac = parent.Faction;
			StrikeAftermathEntry entry = wc.GetOrCreate(parent, StrikeAftermathMode.Salvage);
			entry.expireTick = Find.TickManager.TicksGame + WorldComponent_StrikeAftermath.SalvageTicks;
			entry.ledgerApplied = true;
			entry.garrison.Clear();

			FoldLootIntoEntry(map, entry, tryMinify: MinifyEverythingBridge.IsAvailable());

			// Convert Settlement → DestroyedSettlement when applicable.
			if (parent is Settlement settlement && !settlement.Destroyed)
			{
				try { SettlementDefeatUtility.CheckDefeated(settlement); }
				catch (Exception e) { Log.Warning($"[CrystallizeActiveMaps] CheckDefeated: {e.Message}"); }
			}

			MapParent ruins = Find.WorldObjects?.MapParentAt(entry.tile);
			if (ruins != null)
				wc.RebindTile(entry.tile, ruins);

			UnloadMap(map);

			Messages.Message("Crystallize_AM_SalvageCreated".Translate(entry.loot?.Count ?? 0),
				ruins ?? (WorldObject)parent, MessageTypeDefOf.NeutralEvent, false);
		}

		/// <summary>
		/// Hard unload. Drops only our pin tokens; if a foreign token still holds the map
		/// (quest mods / SOS2 / other ActiveMaps consumers), abort — do not Deinit under them.
		/// </summary>
		/// <returns>true if map was removed (or already null); false if aborted due to foreign pin.</returns>
		public static bool UnloadMap(Map map)
		{
			if (map == null) return true;
			try
			{
				// Already gone (double End / CheckDefeated reparented map) — not an error.
				if (Find.Maps == null || !Find.Maps.Contains(map))
					return true;

				EnsureUiNotOnMap(map);
				ActiveMaps.UnpinOwned(map);

				if (ActiveMaps.IsPinned(map))
				{
					Log.Warning(
						$"[CrystallizeActiveMaps] UnloadMap aborted: map {map.uniqueID} still pinned " +
						$"({ActiveMaps.DescribePins(map)}). Refusing DeinitAndRemoveMap to avoid save corruption.");
					return false;
				}

				if (Find.Maps == null || !Find.Maps.Contains(map))
					return true;

				// Never Deinit while a colonist is on a linked pocket (vanilla would wipe them).
				if (FloorEntranceUtility.HasLivingPlayerOnMapOrLinkedFloors(map))
				{
					ActiveMaps.Pin(map, WorldComponent_StrikeAftermath.PinLinkedFloors, ActiveMapReason.LootWindow);
					Log.Warning(
						$"[CrystallizeActiveMaps] UnloadMap aborted: living player on map/linked floors " +
						$"(map {map.uniqueID}). Pinned linked floors.");
					return false;
				}

				map.GetComponent<MapComponent_StrikeLootWindow>()?.AbortKeepWorldObject();
				map.GetComponent<MapComponent_RuinsHold>()?.Release();
				using (QuestMapRemovedSuppress.MaybeEnter(map))
				using (QuestParkScope.Enter(map))
					Current.Game.DeinitAndRemoveMap(map, notifyPlayer: false);
				return true;
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] Salvage UnloadMap: {e.Message}");
				return false;
			}
		}

		/// <summary>
		/// Before DeinitAndRemoveMap: leave the map if it is CurrentMap,
		/// otherwise UI can desync on a destroyed map.
		/// </summary>
		public static void EnsureUiNotOnMap(Map map)
		{
			if (map == null || Current.Game == null) return;
			try
			{
				bool isCurrent = Find.CurrentMap == map;
				if (isCurrent)
				{
					Map home = Find.AnyPlayerHomeMap;
					if (home != null && home != map)
					{
						Current.Game.CurrentMap = home;
						CameraJumper.TryJump(home.Center, home);
					}
					else
					{
						CameraJumper.TryShowWorld();
						if (Find.CurrentMap == map)
						{
							Map other = null;
							if (Find.Maps != null)
							{
								for (int i = 0; i < Find.Maps.Count; i++)
								{
									if (Find.Maps[i] != null && Find.Maps[i] != map)
									{
										other = Find.Maps[i];
										break;
									}
								}
							}
							if (other != null)
								Current.Game.CurrentMap = other;
						}
					}
				}

				Find.Selector?.ClearSelection();
				try { Find.Targeter?.StopTargeting(); } catch { /* ignore */ }
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] EnsureUiNotOnMap: {e.Message}");
			}
		}
	}

	public static class VirtualGarrisonUtility
	{
		public static void CaptureAlive(Map map, MapParent parent)
		{
			if (!VgProfileResolver.TryResolveForCapture(parent, out VgProfile profile))
				return;
			// EvacuateOnly is Preserve path — not a VG capture.
			if (!profile.Has(VgFlags.FoldDefenders) && !profile.Has(VgFlags.DefenseLedger)
			    && !profile.Has(VgFlags.StructureSketch))
				return;
			VgCapturePipeline.Run(map, parent, profile);
		}

		public static void ApplyOnMapGenerated(Map map)
		{
			// Non-VG: sticky cull + restore Universal Evacuate loot (any mode).
			QuestDestroyedLedger.Apply(map);
			StrikeAftermathEntry entry = WorldComponent_StrikeAftermath.Get()?.GetByWorldObject(map?.Parent);
			if (entry != null)
				QuestSignificantSet.RestoreEvacuatedQuestThings(map, entry);
			VgApplyPipeline.Run(map);
		}
	}
}
