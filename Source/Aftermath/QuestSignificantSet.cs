using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Per-Type cache for QuestPart field walks. GetFields() allocates a new FieldInfo[] every call;
	/// nested quest×part loops must not pay that cost repeatedly.
	/// </summary>
	static class QuestPartFieldCache
	{
		const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		static readonly Dictionary<Type, FieldInfo[]> ByType = new Dictionary<Type, FieldInfo[]>();
		static readonly FieldInfo[] Empty = Array.Empty<FieldInfo>();

		public static FieldInfo[] For(Type type)
		{
			if (type == null) return Empty;
			if (ByType.TryGetValue(type, out FieldInfo[] fields))
				return fields;
			fields = type.GetFields(Flags) ?? Empty;
			ByType[type] = fields;
			return fields;
		}

		public static FieldInfo[] For(object instance) => For(instance?.GetType());
	}

	public static class QuestSignificantSet
	{
		public static void Collect(Map map, Site site, HashSet<Pawn> pawns, HashSet<Thing> things)
		{
			if (map == null) return;
			pawns = pawns ?? new HashSet<Pawn>();
			things = things ?? new HashSet<Thing>();

			CollectFromMapTags(map, pawns, things);
			CollectFromSiteParts(map, site, pawns, things);
			CollectHackables(map, things);

			if (Find.QuestManager == null) return;
			List<Quest> quests = Find.QuestManager.QuestsListForReading;
			for (int i = 0; i < quests.Count; i++)
			{
				Quest q = quests[i];
				if (q == null || q.State != QuestState.Ongoing) continue;
				if (!QuestTouches(q, site, map)) continue;
				CollectFromQuestParts(q, map, pawns, things);
			}
		}

		static void CollectFromSiteParts(Map map, Site site, HashSet<Pawn> pawns, HashSet<Thing> things)
		{
			if (site?.parts == null) return;
			for (int i = 0; i < site.parts.Count; i++)
			{
				SitePart part = site.parts[i];
				if (part == null) continue;

				Thing causer = part.conditionCauser;
				if (causer != null && !causer.Destroyed && causer.Spawned && causer.Map == map)
				{
					if (causer is Pawn cp) pawns.Add(cp);
					else things.Add(causer);
				}

				if (part.things == null) continue;
				for (int t = 0; t < part.things.Count; t++)
				{
					Thing thing = part.things[t];
					if (thing == null || thing.Destroyed || !thing.Spawned || thing.Map != map) continue;
					if (thing is Pawn p) pawns.Add(p);
					else things.Add(thing);
				}
			}
		}

		/// <summary>
		/// Ideology terminals often lack questTags until touched — still quest-critical on Preserve Sites.
		/// Do NOT evacuate every CompHackable on the map (turrets/doors/props): leftovers stay unhacked in loot
		/// and incorrectly keep MapRemoved suppressed after the real terminal is Hacked → Success never fires.
		/// </summary>
		static void CollectHackables(Map map, HashSet<Thing> things)
		{
			if (map?.listerThings == null) return;
			List<Thing> buildings = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
			if (buildings == null) return;
			for (int i = 0; i < buildings.Count; i++)
			{
				Thing t = buildings[i];
				if (t == null || t.Destroyed || !t.Spawned) continue;
				if (!IsQuestCriticalHackable(t)) continue;
				things.Add(t);
			}
		}

		/// <summary>Worshipped/Ancient/Spacedrone objectives — not every hackable building on the tile.</summary>
		public static bool IsQuestCriticalHackable(Thing t)
		{
			if (t == null || t is Building_Door) return false;
			if (t.TryGetComp<CompHackable>() == null) return false;
			if (t.questTags != null && t.questTags.Count > 0) return true;
			string dn = t.def?.defName ?? "";
			if (dn.NullOrEmpty()) return false;
			return dn.IndexOf("Terminal", StringComparison.OrdinalIgnoreCase) >= 0
			       || dn.IndexOf("Spacedrone", StringComparison.OrdinalIgnoreCase) >= 0
			       || dn.IndexOf("Worship", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static void CollectFromMapTags(Map map, HashSet<Pawn> pawns, HashSet<Thing> things)
		{
			IReadOnlyList<Pawn> spawned = map.mapPawns?.AllPawnsSpawned;
			if (spawned != null)
			{
				for (int i = 0; i < spawned.Count; i++)
				{
					Pawn p = spawned[i];
					if (p == null || p.Destroyed) continue;
					if (QuestUtility.IsQuestLodger(p) || (p.questTags != null && p.questTags.Count > 0))
						pawns.Add(p);
				}
			}

			foreach (Thing t in map.listerThings.AllThings)
			{
				if (t == null || t.Destroyed || t is Pawn) continue;
				if (t.questTags != null && t.questTags.Count > 0)
					things.Add(t);
			}
		}

		static bool QuestTouches(Quest quest, Site site, Map map)
		{
			if (site != null)
			{
				foreach (QuestPart part in quest.PartsListForReading)
				{
					if (PartReferencesWorldObject(part, site)) return true;
					if (PartReferencesTile(part, site.Tile)) return true;
				}
			}

			// Tags on map that match any part signal / quest id heuristic.
			string qId = quest.id.ToString();
			foreach (Thing t in map.listerThings.AllThings)
			{
				if (t?.questTags == null) continue;
				for (int i = 0; i < t.questTags.Count; i++)
				{
					string tag = t.questTags[i];
					if (tag != null && (tag.Contains(qId) || tag.StartsWith("Quest")))
						return true;
				}
			}
			return false;
		}

		static void CollectFromQuestParts(Quest quest, Map map, HashSet<Pawn> pawns, HashSet<Thing> things)
		{
			foreach (QuestPart part in quest.PartsListForReading)
			{
				if (part == null) continue;
				try
				{
					FieldInfo[] fields = QuestPartFieldCache.For(part);
					for (int i = 0; i < fields.Length; i++)
					{
						FieldInfo fi = fields[i];
						object val = null;
						try { val = fi.GetValue(part); } catch { continue; }
						if (val == null) continue;
						AddIfOnMap(val, map, pawns, things);
						if (val is System.Collections.IEnumerable en && !(val is string))
						{
							foreach (object o in en)
								AddIfOnMap(o, map, pawns, things);
						}
					}
				}
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] QuestSignificantSet part {part.GetType().Name}: {e.Message}");
				}
			}
		}

		static void AddIfOnMap(object o, Map map, HashSet<Pawn> pawns, HashSet<Thing> things)
		{
			if (o is Pawn p && p.Spawned && p.Map == map)
				pawns.Add(p);
			else if (o is Thing t && !(o is Pawn) && t.Spawned && t.Map == map)
				things.Add(t);
		}

		static bool PartReferencesWorldObject(QuestPart part, WorldObject wo)
		{
			FieldInfo[] fields = QuestPartFieldCache.For(part);
			for (int i = 0; i < fields.Length; i++)
			{
				object val;
				try { val = fields[i].GetValue(part); } catch { continue; }
				if (ReferenceEquals(val, wo)) return true;
				if (val is System.Collections.IEnumerable en && !(val is string))
				{
					foreach (object o in en)
						if (ReferenceEquals(o, wo)) return true;
				}
			}
			return false;
		}

		static bool PartReferencesTile(QuestPart part, PlanetTile tile)
		{
			FieldInfo[] fields = QuestPartFieldCache.For(part);
			for (int i = 0; i < fields.Length; i++)
			{
				FieldInfo fi = fields[i];
				if (fi.FieldType != typeof(PlanetTile) && fi.FieldType != typeof(int)) continue;
				object val;
				try { val = fi.GetValue(part); } catch { continue; }
				if (val is PlanetTile pt && pt == tile) return true;
			}
			return false;
		}

		/// <summary>
		/// Universal quest evacuate: Collect → pawns to Site holder (or WorldPawns), things to holder.
		/// Includes SitePart refs, questTags, QuestPart fields, quest-critical CompHackable
		/// (<see cref="IsQuestCriticalHackable"/> — not every hackable on the map).
		/// Hostages: must return to <see cref="SitePart.things"/> / ImportantPawnComp — GenStep
		/// only reuses those; empty → <c>GeneratePrisoner</c> (new pawn).
		/// </summary>
		public static void Evacuate(Map map, Site site, ThingOwner thingHolder)
		{
			var pawns = new HashSet<Pawn>();
			var things = new HashSet<Thing>();
			Collect(map, site, pawns, things);

			foreach (Pawn p in pawns.ToList())
			{
				if (p == null || p.Destroyed || !p.Spawned) continue;
				try
				{
					ScrubPawnBeforeWorldEvacuate(p);
					p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ExitedMap);
					p.DeSpawn(DestroyMode.Vanish);
					if (!TryStashQuestPawnOnSite(site, p) && !p.IsWorldPawn())
						Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.KeepForever);
				}
				catch (Exception e)
				{
					Log.Warning($"[CrystallizeActiveMaps] Evacuate pawn: {e.Message}");
				}
			}

			if (thingHolder != null)
			{
				foreach (Thing t in things.ToList())
				{
					if (t == null || t.Destroyed || !t.Spawned) continue;
					try
					{
						t.DeSpawn(DestroyMode.Vanish);
						thingHolder.TryAddOrTransfer(t, canMergeWithExistingStacks: true);
					}
					catch (Exception e)
					{
						Log.Warning($"[CrystallizeActiveMaps] Evacuate thing: {e.Message}");
					}
				}
			}
		}

		/// <summary>Alias — same Collect/Evacuate (quest-critical hackables included).</summary>
		public static void EvacuateForQuestVgLight(Map map, Site site, ThingOwner thingHolder)
			=> Evacuate(map, site, thingHolder);

		/// <summary>
		/// Re-place evacuated quest buildings from entry.loot; cull regen duplicates first.
		/// Safe for any aftermath mode (None / VG / Hold leftover).
		/// </summary>
		public static void RestoreEvacuatedQuestThings(Map map, StrikeAftermathEntry entry)
		{
			if (map == null || entry?.loot == null || entry.loot.Count == 0) return;

			var held = new List<Thing>();
			for (int i = 0; i < entry.loot.Count; i++)
			{
				Thing t = entry.loot[i];
				if (t != null && !t.Destroyed && !(t is Pawn))
					held.Add(t);
			}
			if (held.Count == 0) return;

			var heldDefs = new HashSet<string>();
			for (int i = 0; i < held.Count; i++)
			{
				string dn = held[i].def?.defName;
				if (!dn.NullOrEmpty())
					heldDefs.Add(dn);
			}

			bool prevSuppress = QuestDestroyedLedger.SuppressDestroyTracking;
			QuestDestroyedLedger.SuppressDestroyTracking = true;
			try
			{
				List<Thing> buildings = map.listerThings?.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
				if (buildings != null)
				{
					for (int i = buildings.Count - 1; i >= 0; i--)
					{
						Thing t = buildings[i];
						if (t == null || t.Destroyed) continue;
						string dn = t.def?.defName;
						if (dn.NullOrEmpty() || !heldDefs.Contains(dn)) continue;
						if (t.TryGetComp<CompHackable>() == null
						    && (t.questTags == null || t.questTags.Count == 0))
							continue;
						try { t.Destroy(DestroyMode.Vanish); }
						catch (Exception e)
						{
							Log.Warning($"[CrystallizeActiveMaps] Evacuate restore cull regen: {e.Message}");
						}
					}
				}

				int restored = 0;
				for (int i = 0; i < held.Count; i++)
				{
					Thing t = held[i];
					if (t == null || t.Destroyed || t.Spawned) continue;
					try
					{
						if (!entry.loot.Contains(t)) continue;
						entry.loot.Remove(t);
						IntVec3 cell = PreferRestoreCell(t, map);
						GenSpawn.Spawn(t, cell, map, WipeMode.Vanish);
						restored++;
					}
					catch (Exception e)
					{
						Log.Warning($"[CrystallizeActiveMaps] Evacuate restore: {e.Message}");
						try { entry.loot.TryAddOrTransfer(t, canMergeWithExistingStacks: true); }
						catch { /* ignore */ }
					}
				}
				if (restored > 0)
					Log.Message($"[CrystallizeActiveMaps] Restored {restored} evacuated quest thing(s) on {map.Parent?.LabelCap}.");
			}
			finally
			{
				QuestDestroyedLedger.SuppressDestroyTracking = prevSuppress;
			}
		}

		static IntVec3 PreferRestoreCell(Thing t, Map map)
		{
			IntVec3 cell = t.Position;
			if (cell.IsValid && cell.InBounds(map) && CanAcceptBuilding(t, cell, map))
				return cell;

			// Regen layout may block old cell — search near center / old cell.
			IntVec3 root = cell.IsValid && cell.InBounds(map) ? cell : map.Center;
			if (CellFinder.TryFindRandomCellNear(root, map, 16, c => CanAcceptBuilding(t, c, map), out IntVec3 found))
				return found;
			if (CellFinder.TryFindRandomCellNear(map.Center, map, 28, c => CanAcceptBuilding(t, c, map), out found))
				return found;
			return map.Center;
		}

		static bool CanAcceptBuilding(Thing t, IntVec3 cell, Map map)
		{
			if (!cell.InBounds(map)) return false;
			try
			{
				Rot4 rot = t.Rotation.IsValid ? t.Rotation : Rot4.North;
				return GenConstruct.CanPlaceBlueprintAt(t.def, cell, rot, map, godMode: true).Accepted;
			}
			catch
			{
				return cell.Standable(map) || cell.GetEdifice(map) == null;
			}
		}

		/// <summary>
		/// Put despawned quest pawn back where vanilla GenStep looks first:
		/// <see cref="SitePart.things"/> then <see cref="ImportantPawnComp"/>.
		/// After first enter GenStep <c>Take</c>s the pawn out — without this, re-enter calls GeneratePrisoner.
		/// </summary>
		public static bool TryStashQuestPawnOnSite(Site site, Pawn p)
		{
			if (site == null || p == null || p.Destroyed || p.Spawned) return false;

			try
			{
				if (p.IsWorldPawn())
					Find.WorldPawns.RemovePawn(p);

				if (site.parts != null)
				{
					for (int i = 0; i < site.parts.Count; i++)
					{
						SitePart part = site.parts[i];
						if (part == null || !IsLikelyPawnObjectivePart(part)) continue;
						ThingOwner holder = EnsureSitePartThings(part);
						if (holder != null && holder.TryAdd(p, canMergeWithExistingStacks: false))
							return true;
					}
				}

				ImportantPawnComp ipc = site.GetComponent<ImportantPawnComp>();
				if (ipc?.pawn != null && !ipc.pawn.Any
				    && ipc.pawn.TryAdd(p, canMergeWithExistingStacks: false))
					return true;

				if (site.parts != null)
				{
					for (int i = 0; i < site.parts.Count; i++)
					{
						SitePart part = site.parts[i];
						if (part?.things == null || part.things.Any) continue;
						if (part.things.TryAdd(p, canMergeWithExistingStacks: false))
							return true;
					}
				}
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] Stash quest pawn on site: {e.Message}");
			}

			return false;
		}

		static bool IsLikelyPawnObjectivePart(SitePart part)
		{
			string n = part?.def?.defName ?? "";
			string w = part?.def?.workerClass?.Name ?? "";
			return ContainsIgnoreCase(n, "Prisoner")
			       || ContainsIgnoreCase(n, "Hostage")
			       || ContainsIgnoreCase(n, "Refugee")
			       || ContainsIgnoreCase(n, "Kidnap")
			       || ContainsIgnoreCase(w, "Prisoner")
			       || ContainsIgnoreCase(w, "Refugee")
			       || ContainsIgnoreCase(w, "Hostage");
		}

		static ThingOwner EnsureSitePartThings(SitePart part)
		{
			if (part == null) return null;
			if (part.things == null)
				part.things = new ThingOwner<Thing>(part);
			return part.things;
		}

		static bool ContainsIgnoreCase(string hay, string needle)
		{
			if (hay.NullOrEmpty() || needle.NullOrEmpty()) return false;
			return hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		/// <summary>
		/// Before DeSpawn → WorldPawns: clear AI / reservations / draw registration.
		/// Shared by quest evacuate and VG FoldDefender (foreign-pin unload abort safety).
		/// </summary>
		public static void ScrubPawnBeforeWorldEvacuate(Pawn p)
		{
			if (p == null) return;

			try
			{
				p.jobs?.StopAll(ifLayingKeepLaying: false, canReturnToPool: true);
				p.jobs?.ClearQueuedJobs(canReturnToPool: true);
				p.pather?.StopDead();
				Map map = p.Map;
				if (map?.reservationManager != null)
					map.reservationManager.ReleaseAllClaimedBy(p);
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] Evacuate AI scrub: {e.Message}");
			}

			try
			{
				Find.Selector?.Deselect(p);
				if (p.Spawned && p.Map?.dynamicDrawManager != null)
					p.Map.dynamicDrawManager.DeRegisterDrawable(p);
				GlobalTextureAtlasManager.TryMarkPawnFrameSetDirty(p);
				PortraitsCache.SetDirty(p);
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] Evacuate render scrub: {e.Message}");
			}
		}
	}

	/// <summary>Emergency denylist — empty by default. HoldMap / SkipEvacuate for known broken quests.</summary>
	public static class EmergencyDenylist
	{
		public enum Action
		{
			None,
			HoldMap,
			SkipEvacuateOnly
		}

		// Filled only for confirmed bugs + LOCAL_FIXES entry; mods use ActiveMapsApi / Register*.
		static readonly Dictionary<string, Action> byQuestScript = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
		static readonly Dictionary<string, Action> bySitePart = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);

		public static void RegisterQuestScript(string questScriptDefName, Action action)
		{
			if (questScriptDefName.NullOrEmpty() || action == Action.None) return;
			byQuestScript[questScriptDefName] = action;
		}

		public static void UnregisterQuestScript(string questScriptDefName)
		{
			if (questScriptDefName.NullOrEmpty()) return;
			// Dictionary.Remove is a no-op when missing; never throws.
			byQuestScript.Remove(questScriptDefName);
		}

		public static void RegisterSitePart(string sitePartDefName, Action action)
		{
			if (sitePartDefName.NullOrEmpty() || action == Action.None) return;
			bySitePart[sitePartDefName] = action;
		}

		public static void UnregisterSitePart(string sitePartDefName)
		{
			if (sitePartDefName.NullOrEmpty()) return;
			bySitePart.Remove(sitePartDefName);
		}

		public static Action Resolve(Site site, Map map)
		{
			// Snapshot registry keys — Unregister from a nested callback must not mutate
			// the live dictionary while we walk matching entries.
			if (site?.parts != null)
			{
				var partSnap = new List<KeyValuePair<string, Action>>(bySitePart.Count);
				foreach (KeyValuePair<string, Action> kv in bySitePart)
					partSnap.Add(kv);

				for (int i = 0; i < site.parts.Count; i++)
				{
					string defName = site.parts[i]?.def?.defName;
					if (defName == null) continue;
					for (int s = 0; s < partSnap.Count; s++)
					{
						if (!string.Equals(partSnap[s].Key, defName, StringComparison.OrdinalIgnoreCase))
							continue;
						if (partSnap[s].Value != Action.None)
							return partSnap[s].Value;
					}
				}
			}

			if (Find.QuestManager == null) return Action.None;

			var scriptSnap = new List<KeyValuePair<string, Action>>(byQuestScript.Count);
			foreach (KeyValuePair<string, Action> kv in byQuestScript)
				scriptSnap.Add(kv);

			List<Quest> quests = Find.QuestManager.QuestsListForReading;
			for (int i = 0; i < quests.Count; i++)
			{
				Quest q = quests[i];
				if (q?.State != QuestState.Ongoing || q.root == null) continue;
				Action matched = Action.None;
				for (int s = 0; s < scriptSnap.Count; s++)
				{
					if (!string.Equals(scriptSnap[s].Key, q.root.defName, StringComparison.OrdinalIgnoreCase))
						continue;
					matched = scriptSnap[s].Value;
					break;
				}
				if (matched == Action.None) continue;
				if (QuestSignificantSetTouchesSite(q, site)) return matched;
			}
			return Action.None;
		}

		static bool QuestSignificantSetTouchesSite(Quest q, Site site)
		{
			if (site == null) return false;
			foreach (QuestPart part in q.PartsListForReading)
			{
				if (part == null) continue;
				FieldInfo[] fields = QuestPartFieldCache.For(part);
				for (int i = 0; i < fields.Length; i++)
				{
					object val;
					try { val = fields[i].GetValue(part); } catch { continue; }
					if (ReferenceEquals(val, site)) return true;
				}
			}
			return false;
		}
	}
}
