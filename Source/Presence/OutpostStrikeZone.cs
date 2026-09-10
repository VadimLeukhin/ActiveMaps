using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Outpost barrage Fire Mission — same UX as Rimatomics railgun zone:
	/// world tile → load+pin map → local aim (cell/pawn) → keep map → repeat fire → retarget / end.
	/// </summary>
	public static class OutpostStrikeZone
	{
		public const string PinToken = "activemaps.outpost.firemission";

		/// <summary>Max world-grid tiles from outpost to target.</summary>
		public const int MaxWorldRangeTiles = 30;

		public static bool Active;
		public static PlanetTile Tile = PlanetTile.Invalid;
		public static int MapUniqueId = -1;
		public static int OutpostId = -1;
		public static ThingDef ShellDef;
		public static LocalTargetInfo Aim = LocalTargetInfo.Invalid;
		public static bool Firing;
		public static int NextVolleyTick;
		public static string PauseReason;
		public static bool AnyShotFired;

		static readonly List<PendingShell> pending = new List<PendingShell>();

		struct PendingShell
		{
			public int arriveTick;
			public IntVec3 cell;
			public ThingDef shellDef;
		}

		public static bool IsActiveMap(Map map)
		{
			if (!Active || map == null) return false;
			if (MapUniqueId >= 0 && map.uniqueID == MapUniqueId) return true;
			return Tile.Valid && map.Tile == Tile;
		}

		public static bool NeedsControls() => Active;

		public static AbstractOutpost ResolveOutpost()
		{
			if (OutpostId < 0 || Find.WorldObjects == null) return null;
			foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
			{
				if (wo is AbstractOutpost ao && ao.ID == OutpostId && !ao.Destroyed)
					return ao;
			}
			return null;
		}

		public static Map ResolveMap()
		{
			if (!Tile.Valid) return null;
			Map byId = null;
			if (MapUniqueId >= 0 && Find.Maps != null)
			{
				for (int i = 0; i < Find.Maps.Count; i++)
				{
					if (Find.Maps[i].uniqueID == MapUniqueId)
					{
						byId = Find.Maps[i];
						break;
					}
				}
			}
			return byId ?? Current.Game?.FindMap(Tile);
		}

		/// <summary>World tile chosen: load map, pin, jump, ask for local aim.</summary>
		public static bool TryBeginFromWorld(AbstractOutpost outpost, PlanetTile tile, ThingDef shellDef, out string error)
		{
			error = null;
			if (outpost == null || !tile.Valid || shellDef == null)
			{
				error = "Crystallize_AM_MapFailed".Translate();
				return false;
			}
			if (tile == outpost.Tile)
			{
				error = "Crystallize_AM_CannotFireSelf".Translate();
				return false;
			}
			if (outpost.HasMap && outpost.RaidActive)
			{
				error = "Crystallize_AM_CannotFireDuringRaid".Translate();
				return false;
			}
			if (outpost.CountAmmoAvailable(shellDef) <= 0)
			{
				error = "Crystallize_AM_NoAmmo".Translate();
				return false;
			}

			if (MapHibernation.ForbidStrikeOnFragile
			    && MapHibernation.IsFragileMapBoundTile(tile))
			{
				error = "Crystallize_AM_FragileForbidStrike".Translate();
				return false;
			}

			int dist = Find.WorldGrid.TraversalDistanceBetween(
				outpost.Tile, tile, passImpassable: true, maxDist: MaxWorldRangeTiles + 1, false);
			if (dist < 0 || dist > MaxWorldRangeTiles)
			{
				error = "Crystallize_AM_OutOfWorldRange".Translate(MaxWorldRangeTiles);
				return false;
			}

			if (Active && Tile.Valid && Tile != tile)
				EndMission(jumpHome: false);

			Map map = ActiveMaps.EnsureLoadedMap(tile, ActiveMapReason.FireMission);
			if (map == null)
			{
				error = "Crystallize_AM_MapFailed".Translate();
				return false;
			}

			Active = true;
			FireMissionUi.InvalidateStrikeGizmoCache();
			Tile = tile;
			MapUniqueId = map.uniqueID;
			OutpostId = outpost.ID;
			ShellDef = shellDef;
			Aim = LocalTargetInfo.Invalid;
			Firing = false;
			AnyShotFired = false;
			PauseReason = null;
			NextVolleyTick = Find.TickManager.TicksGame;
			outpost.PrimaryAmmoDefName = shellDef.defName;
			outpost.NotifyStrikeMissionStarted();
			StrikeHostilityUtility.ClearSession();

			ActiveMaps.Pin(map, PinToken, ActiveMapReason.FireMission);
			ActiveMaps.JumpToMap(map);
			BeginLocalAim();
			return true;
		}

		public static void BeginLocalAim()
		{
			if (!Active) return;
			Map map = ResolveMap();
			if (map == null)
			{
				Messages.Message("Crystallize_AM_MapFailed".Translate(), MessageTypeDefOf.RejectInput, false);
				return;
			}

			Current.Game.CurrentMap = map;
			CameraJumper.TryHideWorld();

			var parms = new TargetingParameters
			{
				canTargetPawns = true,
				canTargetBuildings = true,
				canTargetLocations = true,
				canTargetSelf = false
			};

			Find.Targeter.BeginTargeting(
				parms,
				OnLocalAimChosen,
				(Pawn)null,
				(System.Action)null,
				FireMissionUi.TargetIcon);
		}

		static void OnLocalAimChosen(LocalTargetInfo x)
		{
			if (!Active || !x.IsValid)
				return;
			Aim = x;
			Firing = true;
			PauseReason = null;
			// First shot after aim: vanilla warmup only (4s); reload applies after the volley.
			NextVolleyTick = Find.TickManager.TicksGame + 240;
			Messages.Message("Crystallize_AM_OutpostAimSet".Translate(DescribeAim(x)),
				x.ToTargetInfo(ResolveMap()), MessageTypeDefOf.NeutralEvent);
		}

		static string DescribeAim(LocalTargetInfo x)
		{
			if (x.HasThing) return x.Thing.LabelShortCap;
			return x.Cell.ToString();
		}

		public static void BeginLocalRetarget() => BeginLocalAim();

		/// <summary>Stop current aim/firing only — map stays pinned, mission stays active.</summary>
		public static void StopFire()
		{
			if (!Active) return;
			try { Find.Targeter?.StopTargeting(); } catch { /* ignore */ }
			Aim = LocalTargetInfo.Invalid;
			Firing = false;
			PauseReason = "Crystallize_AM_OutpostNeedAim".Translate();
			Messages.Message("Crystallize_AM_StopFire".Translate(), MessageTypeDefOf.NeutralEvent, false);
		}

		public static void EndMission(bool jumpHome = true)
		{
			Map map = ResolveMap();
			if (map != null)
			{
				bool anyShot = AnyShotFired;
				StrikeMapAftermath.OnEndStrike(map, new StrikeEndContext
				{
					AnyShotFired = anyShot,
					PinTokenMission = PinToken,
					PinTokenLoot = null,
					ClearConditions = null
				});
			}

			AbstractOutpost outpost = ResolveOutpost();
			outpost?.NotifyStrikeMissionEnded();

			Active = false;
			FireMissionUi.InvalidateStrikeGizmoCache();
			Tile = PlanetTile.Invalid;
			MapUniqueId = -1;
			OutpostId = -1;
			ShellDef = null;
			Aim = LocalTargetInfo.Invalid;
			Firing = false;
			AnyShotFired = false;
			PauseReason = null;
			pending.Clear();
			StrikeHostilityUtility.ClearSession();

			if (jumpHome)
			{
				try
				{
					Map home = Find.AnyPlayerHomeMap;
					if (home != null && (Find.CurrentMap == null || Find.CurrentMap == map || !Find.CurrentMap.IsPlayerHome))
					{
						Current.Game.CurrentMap = home;
						CameraJumper.TryJump(home.Center, home);
					}
				}
				catch { /* ignore */ }
				Messages.Message("Crystallize_AM_StrikeEnded".Translate(), MessageTypeDefOf.NeutralEvent, false);
			}
		}

		public static void Tick()
		{
			ResolvePendingImpacts();
			if (!Active || !Firing) return;

			Map map = ResolveMap();
			AbstractOutpost outpost = ResolveOutpost();
			if (map == null || outpost == null || outpost.Destroyed)
			{
				EndMission(jumpHome: true);
				return;
			}

			if (!AimStillValid(map, out string pause))
			{
				if (Firing)
				{
					Firing = false;
					PauseReason = pause;
					Messages.Message(pause, MessageTypeDefOf.NeutralEvent, false);
				}
				return;
			}

			if (ShellDef == null || outpost.CountAmmoAvailable(ShellDef) <= 0)
			{
				Firing = false;
				PauseReason = "Crystallize_AM_NoAmmo".Translate();
				Messages.Message(PauseReason, MessageTypeDefOf.RejectInput, false);
				return;
			}

			int now = Find.TickManager.TicksGame;
			if (now < NextVolleyTick) return;

			int mortars = Mathf.Max(1, outpost.Ledger.Get("Turret_Mortar"));
			mortars = Mathf.Min(mortars, 6);
			int fired = 0;
			for (int i = 0; i < mortars; i++)
			{
				if (outpost.CountAmmoAvailable(ShellDef) <= 0) break;
				if (!outpost.TryConsumeAmmo(ShellDef, 1)) break;
				IntVec3 cell = ResolveImpactCell(map, Aim, scatter: true);
				// Stagger tubes slightly inside the volley (not the 28s reload).
				LaunchShell(outpost, map, cell, ShellDef, stagger: i * 12);
				fired++;
			}

			if (fired > 0)
			{
				AnyShotFired = true;
				WorldComponent_StrikeAftermath.MarkShelled(map);
				StrikeHostilityUtility.NotifyPlayerAttackedMap(map);
			}

			// Vanilla Turret_Mortar: warmupTime 4.0s + building.turretBurstCooldownTime 28.0s.
			// Battery volley = all tubes fire once, then one shared reload like manned mortars.
			const int WarmupTicks = 240;   // 4s
			const int ReloadTicks = 1680;  // 28s
			NextVolleyTick = now + WarmupTicks + ReloadTicks;

			if (fired > 0 && outpost.CountAmmoAvailable(ShellDef) <= 0)
			{
				Firing = false;
				PauseReason = "Crystallize_AM_NoAmmo".Translate();
			}
		}

		static bool AimStillValid(Map map, out string reason)
		{
			reason = null;
			if (!Aim.IsValid)
			{
				reason = "Crystallize_AM_OutpostNeedAim".Translate();
				return false;
			}
			if (!Aim.HasThing)
				return true; // bare cell — keep firing

			Thing t = Aim.Thing;
			if (t == null || t.Destroyed || t.Map != map)
			{
				reason = "Crystallize_AM_OutpostTargetGone".Translate();
				return false;
			}
			if (t is Pawn p && (p.Dead || p.Destroyed))
			{
				reason = "Crystallize_AM_OutpostTargetGone".Translate();
				return false;
			}
			return true;
		}

		static IntVec3 ResolveImpactCell(Map map, LocalTargetInfo aim, bool scatter)
		{
			IntVec3 cell = aim.HasThing ? aim.Thing.PositionHeld : aim.Cell;
			if (!cell.InBounds(map))
				cell = map.Center;
			if (scatter)
			{
				cell = cell + GenRadial.RadialPattern[Rand.Range(0, Mathf.Min(12, GenRadial.RadialPattern.Length))];
				if (!cell.InBounds(map))
					cell = aim.Cell.InBounds(map) ? aim.Cell : map.Center;
			}
			return cell;
		}

		static void LaunchShell(AbstractOutpost outpost, Map map, IntVec3 cell, ThingDef shellDef, int stagger)
		{
			int travel = Mathf.Max(90, Find.WorldGrid.TraversalDistanceBetween(outpost.Tile, map.Tile, passImpassable: true, maxDist: 999) * 8);
			travel += stagger;

			// Visible world tracer (one shell).
			var strike = (TravellingOutpostStrike)WorldObjectMaker.MakeWorldObject(
				DefDatabase<WorldObjectDef>.GetNamedSilentFail("Crystallize_TravellingOutpostStrike"));
			if (strike != null)
			{
				int now = Find.TickManager.TicksGame;
				strike.Tile = outpost.Tile;
				strike.startTile = outpost.Tile;
				strike.destinationTile = map.Tile;
				strike.targetCell = cell;
				strike.shellDef = shellDef;
				strike.shellCount = 1;
				strike.sourceOutpostId = outpost.ID;
				strike.departTick = now;
				strike.arrivalTick = now + travel;
				strike.SetFaction(Faction.OfPlayer);
				Find.WorldObjects.Add(strike);
			}
			else
			{
				pending.Add(new PendingShell
				{
					arriveTick = Find.TickManager.TicksGame + travel,
					cell = cell,
					shellDef = shellDef
				});
			}
		}

		static void ResolvePendingImpacts()
		{
			if (pending.Count == 0) return;
			Map map = ResolveMap();
			int now = Find.TickManager.TicksGame;
			for (int i = pending.Count - 1; i >= 0; i--)
			{
				if (pending[i].arriveTick > now) continue;
				PendingShell s = pending[i];
				pending.RemoveAt(i);
				if (map != null)
					TravellingOutpostStrike.DetonateAt(map, s.cell, s.shellDef);
			}
		}

		public static string InspectExtra()
		{
			if (!Active) return null;
			var sb = new System.Text.StringBuilder();
			sb.Append("Crystallize_AM_OutpostMissionActive".Translate());
			if (Aim.IsValid)
				sb.AppendLine().Append("Crystallize_AM_OutpostAim".Translate(DescribeAim(Aim)));
			if (!Firing && !PauseReason.NullOrEmpty())
				sb.AppendLine().Append(PauseReason);
			AbstractOutpost o = ResolveOutpost();
			if (o != null && ShellDef != null)
				sb.AppendLine().Append("Crystallize_AM_AmmoAvailable".Translate(o.CountAmmoAvailable(ShellDef), ShellDef.label));
			return sb.ToString().TrimEnd();
		}
	}

	/// <summary>Ticks outpost Fire Mission volleys.</summary>
	public class WorldComponent_OutpostStrike : WorldComponent
	{
		IntVec3 savedAimCell = IntVec3.Invalid;
		Thing savedAimThing;

		public WorldComponent_OutpostStrike(World world) : base(world) { }

		public override void WorldComponentTick()
		{
			base.WorldComponentTick();
			OutpostStrikeZone.Tick();
		}

		public override void ExposeData()
		{
			base.ExposeData();
			bool active = OutpostStrikeZone.Active;
			PlanetTile tile = OutpostStrikeZone.Tile;
			int mapId = OutpostStrikeZone.MapUniqueId;
			int outpostId = OutpostStrikeZone.OutpostId;
			ThingDef shell = OutpostStrikeZone.ShellDef;
			bool firing = OutpostStrikeZone.Firing;
			int next = OutpostStrikeZone.NextVolleyTick;
			string pause = OutpostStrikeZone.PauseReason;

			if (Scribe.mode == LoadSaveMode.Saving)
			{
				savedAimCell = OutpostStrikeZone.Aim.IsValid ? OutpostStrikeZone.Aim.Cell : IntVec3.Invalid;
				savedAimThing = OutpostStrikeZone.Aim.Thing;
			}

			Scribe_Values.Look(ref active, "osActive", false);
			Scribe_Values.Look(ref tile, "osTile");
			Scribe_Values.Look(ref mapId, "osMapId", -1);
			Scribe_Values.Look(ref outpostId, "osOutpostId", -1);
			Scribe_Defs.Look(ref shell, "osShell");
			Scribe_Values.Look(ref firing, "osFiring", false);
			Scribe_Values.Look(ref next, "osNext", 0);
			Scribe_Values.Look(ref pause, "osPause");
			Scribe_Values.Look(ref savedAimCell, "osAimCell", IntVec3.Invalid);
			Scribe_References.Look(ref savedAimThing, "osAimThing");

			if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				OutpostStrikeZone.Active = active;
				OutpostStrikeZone.Tile = tile;
				OutpostStrikeZone.MapUniqueId = mapId;
				OutpostStrikeZone.OutpostId = outpostId;
				OutpostStrikeZone.ShellDef = shell;
				OutpostStrikeZone.Firing = firing;
				OutpostStrikeZone.NextVolleyTick = next;
				OutpostStrikeZone.PauseReason = pause;
				if (savedAimThing != null)
					OutpostStrikeZone.Aim = new LocalTargetInfo(savedAimThing);
				else if (savedAimCell.IsValid)
					OutpostStrikeZone.Aim = new LocalTargetInfo(savedAimCell);
				else
					OutpostStrikeZone.Aim = LocalTargetInfo.Invalid;
			}
		}
	}
}
