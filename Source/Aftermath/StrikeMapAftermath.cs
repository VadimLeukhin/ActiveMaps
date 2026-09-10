using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	public static class StrikeMapAftermath
	{
		public static void OnEndStrike(Map map, StrikeEndContext ctx)
		{
			if (map == null)
				return;

			MapParent parent = map.Parent;
			if (parent == null || parent.Destroyed)
			{
				TryUnloadOnly(map, ctx);
				return;
			}

			Unpin(map, ctx);

			if (!ctx.AnyShotFired)
			{
				HandleNoShots(map, parent, ctx);
				return;
			}

			StrikeMapClass cls = StrikeMapClassifier.Classify(parent);
			try
			{
				ctx.ClearConditions?.Invoke(map, parent);
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] ClearConditions: {e.Message}");
			}

			switch (cls)
			{
				case StrikeMapClass.PlayerOwned:
					// Leave map; caller jumps home.
					return;

				case StrikeMapClass.EphemeralEmpty:
					HandleEphemeralEmpty(map, parent);
					return;

				case StrikeMapClass.PreserveSite:
					HandlePreserveSite(map, parent as Site, ctx);
					return;

				case StrikeMapClass.Ruins:
					// Already ruins — migrate to salvage if map still up.
					StrikeSalvageUtility.ApplySalvageDefeated(map, parent);
					return;

				case StrikeMapClass.Habitation:
					HandleHabitation(map, parent);
					return;

				default:
					StrikeSalvageUtility.UnloadMap(map);
					return;
			}
		}

		static void HandleNoShots(Map map, MapParent parent, StrikeEndContext ctx)
		{
			StrikeMapClass cls = StrikeMapClassifier.Classify(parent);
			// Peek/End with no shells: re-capture VG, never convert Settlement→ruins.
			// (Vanilla IsDefeated ignores mechs — DMS etc. looked "defeated" on open.)
			if (cls == StrikeMapClass.Habitation)
			{
				if (FloorEntranceUtility.HasLivingPlayerOnMapOrLinkedFloors(map))
				{
					ActiveMaps.Pin(map, WorldComponent_StrikeAftermath.PinHoldMap, ActiveMapReason.LootWindow);
					Messages.Message("Crystallize_AM_PreserveHoldMap".Translate(), parent, MessageTypeDefOf.NeutralEvent, false);
					return;
				}
				// CaptureAlive already UnloadMap — do not call twice ("Tried to remove map…").
				VirtualGarrisonUtility.CaptureAlive(map, parent);
				return;
			}

			// Same evacuate/denylist path as shot Preserve (Phase 1 EvacuateOnly helper).
			if (cls == StrikeMapClass.PreserveSite)
			{
				HandlePreserveSite(map, parent as Site, ctx);
				return;
			}

			StrikeSalvageUtility.UnloadMap(map);
			if (cls == StrikeMapClass.EphemeralEmpty && parent != null && !parent.Destroyed)
			{
				try { parent.Destroy(); }
				catch (Exception e) { Log.Warning($"[CrystallizeActiveMaps] Destroy empty temp: {e.Message}"); }
			}
		}

		static void HandleEphemeralEmpty(Map map, MapParent parent)
		{
			StrikeSalvageUtility.UnloadMap(map);
			if (parent == null || parent.Destroyed) return;
			// Destroy disposable hosts (ours or blank foreign shell) — not story Sites with parts.
			if (!StrikeMapClassifier.IsEphemeralStrikeHost(parent)
			    && !StrikeMapClassifier.IsLikelyDisposableMapParent(parent))
				return;
			try { parent.Destroy(); }
			catch (Exception e) { Log.Warning($"[CrystallizeActiveMaps] Destroy temp site: {e.Message}"); }
		}

		static void HandlePreserveSite(Map map, Site site, StrikeEndContext ctx)
		{
			EmergencyDenylist.Action deny = EmergencyDenylist.Resolve(site, map);

			// Map-bound only (Anomaly distress): Hibernate (skip ticks) — cannot Universal Evacuate safely.
			if (deny == EmergencyDenylist.Action.HoldMap
			    || PreservePeekSafety.ShouldHoldOnPeek(site, map))
			{
				HibernateOrHoldPreserveMap(map, site);
				return;
			}

			// Hostage destroyed → fail quests + remove Site. Terminals: vanilla Ideo signals only.
			if (site != null && QuestDestroyedLedger.HasCriticalObjectiveDestroyed(map, site))
			{
				OrphanSiteCleanup.OnCriticalObjectiveDestroyed(site);
				if (site.Destroyed)
					Messages.Message("Crystallize_AM_SiteObjectiveLost".Translate(), MessageTypeDefOf.NeutralEvent, false);
				else
					Messages.Message("Crystallize_AM_SiteUnloaded".Translate(), site, MessageTypeDefOf.NeutralEvent, false);
				return;
			}

			// QuestVgLight on all Preserve after first shell — except Hibernate/Hold above.
			if (site != null)
			{
				if (ctx.AnyShotFired)
					WorldComponent_StrikeAftermath.MarkShelled(site);
				if (WorldComponent_StrikeAftermath.ShellsLanded(site))
				{
					VirtualGarrisonUtility.CaptureAlive(map, site);
					return;
				}
			}

			// Universal Evacuate + Unload (Peek before any shells).
			if (site != null)
			{
				var wc = WorldComponent_StrikeAftermath.Get();
				// Keep sticky shellsLanded: GetOrCreate only changes mode, not the bit.
				StrikeAftermathEntry e = wc?.GetOrCreate(site, StrikeAftermathMode.None);
				QuestDestroyedLedger.CaptureAll(map, site, e);
				if (deny != EmergencyDenylist.Action.SkipEvacuateOnly)
					QuestSignificantSet.Evacuate(map, site, e?.loot);
			}

			StrikeSalvageUtility.UnloadMap(map);
			Messages.Message("Crystallize_AM_SiteUnloaded".Translate(), site, MessageTypeDefOf.NeutralEvent, false);
		}

		static void HibernateOrHoldPreserveMap(Map map, Site site)
		{
			if (MapHibernation.PreferHibernate)
			{
				MapHibernation.Enter(map, site);
				Messages.Message("Crystallize_AM_PreserveHibernateMap".Translate(), site, MessageTypeDefOf.NeutralEvent, false);
			}
			else
			{
				MapHibernation.EnterHoldFallback(map, site);
				Messages.Message("Crystallize_AM_PreserveHoldMap".Translate(), site, MessageTypeDefOf.NeutralEvent, false);
			}
		}

		static void HoldPreserveMap(Map map, Site site)
		{
			MapHibernation.EnterHoldFallback(map, site);
			Messages.Message("Crystallize_AM_PreserveHoldMap".Translate(), site, MessageTypeDefOf.NeutralEvent, false);
		}

		static void HandleHabitation(Map map, MapParent parent)
		{
			if (FloorEntranceUtility.HasLivingPlayerOnMapOrLinkedFloors(map))
			{
				// Player on surface or linked pocket — do not unload under them.
				ActiveMaps.Pin(map, WorldComponent_StrikeAftermath.PinHoldMap, ActiveMapReason.LootWindow);
				Messages.Message("Crystallize_AM_PreserveHoldMap".Translate(), parent, MessageTypeDefOf.NeutralEvent, false);
				return;
			}

			// Multi-floor: never Salvage/DestroyedSettlement — would drop portal + pocket access.
			if (FloorEntranceUtility.MapHasFloorEntrance(map)
			    || FloorEntranceUtility.MapHasLinkedPocketMaps(map))
			{
				VirtualGarrisonUtility.CaptureAlive(map, parent);
				return;
			}

			bool defeated = StrikeMapClassifier.IsHabitationDefeated(map, parent);
			if (defeated)
				StrikeSalvageUtility.ApplySalvageDefeated(map, parent);
			else
				VirtualGarrisonUtility.CaptureAlive(map, parent);
		}

		static void Unpin(Map map, StrikeEndContext ctx)
		{
			if (!ctx.PinTokenMission.NullOrEmpty())
				ActiveMaps.Unpin(map, ctx.PinTokenMission);
			if (!ctx.PinTokenLoot.NullOrEmpty())
				ActiveMaps.Unpin(map, ctx.PinTokenLoot);
			ActiveMaps.Unpin(map, OutpostStrikeZone.PinToken);
			ActiveMaps.Unpin(map, RailgunStrikeZone.PinTokenMission);
			ActiveMaps.Unpin(map, RailgunStrikeZone.PinTokenLoot);
		}

		static void TryUnloadOnly(Map map, StrikeEndContext ctx)
		{
			Unpin(map, ctx);
			// UnloadMap refuses Deinit if foreign pins remain (quest/SOS2/etc.).
			StrikeSalvageUtility.UnloadMap(map);
		}
	}
}
