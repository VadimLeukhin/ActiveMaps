using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>Single shell tracer on the world map; detonates at <see cref="targetCell"/> on arrival.</summary>
	public class TravellingOutpostStrike : WorldObject
	{
		public PlanetTile startTile;
		public PlanetTile destinationTile;
		public IntVec3 targetCell = IntVec3.Invalid;
		public ThingDef shellDef;
		public int shellCount = 1;
		public int sourceOutpostId = -1;
		public int departTick;
		public int arrivalTick;

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Values.Look(ref startTile, "startTile");
			Scribe_Values.Look(ref destinationTile, "destinationTile");
			Scribe_Values.Look(ref targetCell, "targetCell", IntVec3.Invalid);
			Scribe_Defs.Look(ref shellDef, "shellDef");
			Scribe_Values.Look(ref shellCount, "shellCount", 1);
			Scribe_Values.Look(ref sourceOutpostId, "sourceOutpostId", -1);
			Scribe_Values.Look(ref departTick, "departTick", 0);
			Scribe_Values.Look(ref arrivalTick, "arrivalTick", 0);
		}

		public override Vector3 DrawPos
		{
			get
			{
				if (!startTile.Valid || !destinationTile.Valid)
					return base.DrawPos;
				float t = 0f;
				if (arrivalTick > departTick)
					t = Mathf.Clamp01((Find.TickManager.TicksGame - departTick) / (float)(arrivalTick - departTick));
				Vector3 a = Find.WorldGrid.GetTileCenter(startTile);
				Vector3 b = Find.WorldGrid.GetTileCenter(destinationTile);
				return Vector3.Slerp(a.normalized, b.normalized, t) * Mathf.Lerp(a.magnitude, b.magnitude, t);
			}
		}

		protected override void Tick()
		{
			base.Tick();
			if (Find.TickManager.TicksGame < arrivalTick) return;
			Arrive();
		}

		void Arrive()
		{
			Map map = Current.Game?.FindMap(destinationTile);
			if (map == null && OutpostStrikeZone.Active && OutpostStrikeZone.Tile == destinationTile)
				map = OutpostStrikeZone.ResolveMap();
			if (map == null)
				map = ActiveMaps.EnsureLoadedMap(destinationTile, ActiveMapReason.FireMission);

			if (map != null && shellDef != null)
			{
				IntVec3 cell = targetCell.IsValid && targetCell.InBounds(map)
					? targetCell
					: DropCellFinder.RandomDropSpot(map);

				for (int i = 0; i < Mathf.Max(1, shellCount); i++)
				{
					IntVec3 c = cell;
					if (i > 0)
						c = cell + GenRadial.RadialPattern[Rand.Range(0, 8)];
					if (!c.InBounds(map)) c = cell;
					DetonateAt(map, c, shellDef);
				}
			}

			Destroy();
		}

		public static void DetonateAt(Map map, IntVec3 cell, ThingDef shellDef)
		{
			if (map == null || shellDef == null || !cell.InBounds(map)) return;
			StrikeHostilityUtility.NotifyPlayerAttackedMap(map);

			ThingDef projDef = shellDef.projectileWhenLoaded;
			ProjectileProperties props = projDef?.projectile;
			DamageDef dmg = props?.damageDef ?? DamageDefOf.Bomb;
			float radius = props != null && props.explosionRadius > 0.1f ? props.explosionRadius : 2.9f;
			int damAmount = props != null ? props.GetDamageAmount(1f, null) : -1;
			SoundDef explodeSound = props?.soundExplode;
			ThingDef filth = props?.preExplosionSpawnSingleThingDef ?? props?.preExplosionSpawnThingDef;
			Thing instigator = StrikeHostilityUtility.TryPlayerInstigator();

			GenExplosion.DoExplosion(
				cell,
				map,
				radius,
				dmg,
				instigator: instigator,
				damAmount: damAmount,
				armorPenetration: -1f,
				explosionSound: explodeSound,
				weapon: null,
				projectile: projDef,
				intendedTarget: null,
				postExplosionSpawnThingDef: filth,
				postExplosionSpawnChance: filth != null ? 1f : 0f,
				postExplosionSpawnThingCount: 1,
				chanceToStartFire: props != null && props.ai_IsIncendiary ? 1f : 0f);
		}
	}

	public static class OutpostRaidUtility
	{
		static readonly List<System.Action<AbstractOutpost, Map>> sketchProviders =
			new List<System.Action<AbstractOutpost, Map>>();

		public static void RegisterSketchProvider(System.Action<AbstractOutpost, Map> provider)
		{
			if (provider == null) return;
			if (!sketchProviders.Contains(provider))
				sketchProviders.Add(provider);
		}

		public static void UnregisterSketchProvider(System.Action<AbstractOutpost, Map> provider)
		{
			if (provider == null) return;
			sketchProviders.Remove(provider);
		}

		public static void NotifySketchProviders(AbstractOutpost outpost, Map map)
		{
			// Per-call snapshot — shared static scratch is not reentrancy-safe.
			var snap = new List<System.Action<AbstractOutpost, Map>>(sketchProviders.Count);
			for (int i = 0; i < sketchProviders.Count; i++)
				snap.Add(sketchProviders[i]);

			for (int i = 0; i < snap.Count; i++)
			{
				System.Action<AbstractOutpost, Map> a = snap[i];
				if (a == null) continue;
				try { a(outpost, map); }
				catch (System.Exception e) { Log.Warning($"[CrystallizeActiveMaps] Sketch provider failed: {e.Message}"); }
			}
		}

		public static void SendRaidLetter(AbstractOutpost outpost)
		{
			if (outpost == null) return;
			Faction enemy = Find.FactionManager.RandomEnemyFaction(allowHidden: false, allowDefeated: false, allowNonHumanlike: false);
			if (enemy == null)
			{
				foreach (Faction f in Find.FactionManager.AllFactionsVisible)
				{
					if (f.HostileTo(Faction.OfPlayer))
					{
						enemy = f;
						break;
					}
				}
			}
			ChoiceLetter_OutpostRaid letter = (ChoiceLetter_OutpostRaid)LetterMaker.MakeLetter(
				"Crystallize_AM_RaidLetterLabel".Translate(outpost.LabelCap),
				"Crystallize_AM_RaidLetterText".Translate(outpost.LabelCap, enemy?.Name ?? "?"),
				DefDatabase<LetterDef>.GetNamedSilentFail("Crystallize_OutpostRaid") ?? LetterDefOf.ThreatBig,
				outpost);
			letter.outpost = outpost;
			letter.raidFaction = enemy;
			Find.LetterStack.ReceiveLetter(letter);
		}

		public static void ResolveAbstractRaid(AbstractOutpost outpost, Faction attacker)
		{
			if (outpost == null) return;
			int garrison = outpost.Occupants.Count + outpost.Ledger.Get("Artillery") + outpost.Ledger.Get("Mortar");
			int threat = Rand.RangeInclusive(2, 6);
			float ratio = garrison <= 0 ? 99f : threat / (float)System.Math.Max(1, garrison);

			int pawnsLost = 0;
			if (ratio > 1.2f && outpost.Occupants.Count > 0)
			{
				pawnsLost = Rand.RangeInclusive(0, System.Math.Min(2, outpost.Occupants.Count));
				for (int i = 0; i < pawnsLost; i++)
				{
					Pawn p = outpost.Occupants.RandomElement();
					outpost.Occupants.Remove(p);
					p.Destroy(DestroyMode.Vanish);
				}
			}

			string ammo = outpost.PrimaryAmmoDefName ?? "Shell_HighExplosive";
			ThingDef shell = DefDatabase<ThingDef>.GetNamedSilentFail(ammo);
			int ammoLost = 0;
			if (shell != null)
			{
				ammoLost = Rand.RangeInclusive(0, System.Math.Min(outpost.CountAmmoAvailable(shell), 5));
				if (ammoLost > 0)
					outpost.TryConsumeAmmo(shell, ammoLost);
			}

			Find.LetterStack.ReceiveLetter(
				"Crystallize_AM_RaidAbstractLabel".Translate(),
				"Crystallize_AM_RaidAbstractText".Translate(outpost.LabelCap, pawnsLost, ammoLost),
				LetterDefOf.NegativeEvent,
				outpost);
		}
	}

	public class ChoiceLetter_OutpostRaid : ChoiceLetter
	{
		public AbstractOutpost outpost;
		public Faction raidFaction;

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_References.Look(ref outpost, "outpost");
			Scribe_References.Look(ref raidFaction, "raidFaction");
		}

		public override IEnumerable<DiaOption> Choices
		{
			get
			{
				DiaOption accept = new DiaOption("Crystallize_AM_RaidAccept".Translate());
				accept.action = () =>
				{
					if (outpost != null && !outpost.Destroyed)
					{
						Map map = outpost.PromoteForRaid(raidFaction);
						if (map != null && raidFaction != null)
						{
							IncidentParms parms = new IncidentParms
							{
								target = map,
								faction = raidFaction,
								points = StorytellerUtility.DefaultThreatPointsNow(map) * 0.65f,
								raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn,
								raidStrategy = RaidStrategyDefOf.ImmediateAttack
							};
							IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
						}
					}
					Find.LetterStack.RemoveLetter(this);
				};
				accept.resolveTree = true;
				yield return accept;

				DiaOption decline = new DiaOption("Crystallize_AM_RaidDecline".Translate());
				decline.action = () =>
				{
					OutpostRaidUtility.ResolveAbstractRaid(outpost, raidFaction);
					Find.LetterStack.RemoveLetter(this);
				};
				decline.resolveTree = true;
				yield return decline;
			}
		}
	}
}
