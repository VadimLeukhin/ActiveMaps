using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	public enum StrikeAftermathMode : byte
	{
		None = 0,
		VirtualGarrison = 1,
		Salvage = 2,
		PreserveHold = 3
	}

	public class StrikeAftermathEntry : IExposable, IThingHolder
	{
		public int worldObjectId = -1;
		public PlanetTile tile = PlanetTile.Invalid;
		public StrikeAftermathMode mode;
		public List<Pawn> garrison = new List<Pawn>();
		public ThingOwner<Thing> loot;
		/// <summary>Enemy defense buildings that survived the strike, keyed by ThingDef.defName.</summary>
		public Dictionary<string, int> survivingDefenseByDef = new Dictionary<string, int>();
		/// <summary>Anchor-relative shell damage captured at End (walls/doors/roof holders).</summary>
		public List<StructureSketchEntry> structureSketch = new List<StructureSketchEntry>();
		public bool ledgerApplied;
		public int expireTick = -1;
		public bool holdMapPinned;
		/// <summary>
		/// Map-bound Preserve: pin stays, but Map/Thing ticks skip until living player/strike wakes the map (phase C).
		/// </summary>
		public bool hibernating;
		/// <summary>World-time facts while hibernating (C2).</summary>
		public List<SiteFact> siteFacts = new List<SiteFact>();
		/// <summary>
		/// Multi-floor: surface map kept loaded (pin) so portal buildings and pocket maps stay linked.
		/// VG ledger/regen path skipped — map state is already honest.
		/// </summary>
		public bool keepLinkedFloors;

		/// <summary>VG profile schema (1 = profileId/flags present).</summary>
		public int vgSchemaVersion;
		/// <summary>See <see cref="VgProfileIds"/>. Empty + VirtualGarrison → Habitation on load.</summary>
		public string vgProfileId;
		/// <summary>Bitset of <see cref="VgFlags"/> baked at capture.</summary>
		public int vgFlags;

		/// <summary>
		/// Quest-significant Things/Pawns destroyed during strike (Hack terminal, hostage, …).
		/// Non-VG sticky: cull matching regen on GetOrGenerateMap. Relic/causer already sticky via SitePart.
		/// </summary>
		public List<QuestDestroyedRecord> questDestroyed = new List<QuestDestroyedRecord>();

		/// <summary>
		/// Sticky: first shell on this world object. Survives Peek Evacuate / mode=None.
		/// QuestVgLight End uses this — not the current strike's AnyShotFired alone.
		/// </summary>
		public bool shellsLanded;

		/// <summary>
		/// LoadingVars copy of defenseByDef. Save-opt mods may clear/reinit Value+Value
		/// dicts during ResolvingCrossRefs; restore instead of a second Look.
		/// </summary>
		[Unsaved]
		Dictionary<string, int> defenseByDefLoadSnapshot;

		public StrikeAftermathEntry()
		{
			loot = new ThingOwner<Thing>(this);
		}

		public IThingHolder ParentHolder => Find.World?.GetComponent<WorldComponent_StrikeAftermath>();

		public void GetChildHolders(List<IThingHolder> outChildren)
		{
			ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
		}

		public ThingOwner GetDirectlyHeldThings() => loot;

		public void ExposeData()
		{
			Scribe_Values.Look(ref worldObjectId, "worldObjectId", -1);
			Scribe_Values.Look(ref tile, "tile");
			Scribe_Values.Look(ref mode, "mode", StrikeAftermathMode.None);
			Scribe_Collections.Look(ref garrison, "garrison", LookMode.Reference);
			if (Scribe.mode == LoadSaveMode.LoadingVars && loot == null)
				loot = new ThingOwner<Thing>(this);
			Scribe_Deep.Look(ref loot, "loot", this);
			if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.LoadingVars)
				Scribe_Collections.Look(ref survivingDefenseByDef, "defenseByDef", LookMode.Value, LookMode.Value);
			if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.LoadingVars)
				Scribe_Collections.Look(ref structureSketch, "structureSketch", LookMode.Deep);
			Scribe_Values.Look(ref ledgerApplied, "ledgerApplied", false);
			Scribe_Values.Look(ref expireTick, "expireTick", -1);
			Scribe_Values.Look(ref holdMapPinned, "holdMapPinned", false);
			Scribe_Values.Look(ref hibernating, "hibernating", false);
			Scribe_Collections.Look(ref siteFacts, "siteFacts", LookMode.Deep);
			Scribe_Values.Look(ref keepLinkedFloors, "keepLinkedFloors", false);
			Scribe_Values.Look(ref vgSchemaVersion, "vgSchemaVersion", 0);
			Scribe_Values.Look(ref vgProfileId, "vgProfileId");
			Scribe_Values.Look(ref vgFlags, "vgFlags", 0);
			Scribe_Collections.Look(ref questDestroyed, "questDestroyed", LookMode.Deep);
			Scribe_Values.Look(ref shellsLanded, "shellsLanded", false);

			if (Scribe.mode == LoadSaveMode.LoadingVars)
			{
				SanitizeDefenseByDef();
				defenseByDefLoadSnapshot = new Dictionary<string, int>(survivingDefenseByDef);
				if (structureSketch == null)
					structureSketch = new List<StructureSketchEntry>();
				if (questDestroyed == null)
					questDestroyed = new List<QuestDestroyedRecord>();
				if (siteFacts == null)
					siteFacts = new List<SiteFact>();
			}
			else if (Scribe.mode == LoadSaveMode.ResolvingCrossRefs
			         || Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				RestoreDefenseByDefIfClobbered();
				if (structureSketch == null)
					structureSketch = new List<StructureSketchEntry>();
				if (questDestroyed == null)
					questDestroyed = new List<QuestDestroyedRecord>();
				if (siteFacts == null)
					siteFacts = new List<SiteFact>();
				if (Scribe.mode == LoadSaveMode.PostLoadInit)
				{
					defenseByDefLoadSnapshot = null;
					if (garrison == null) garrison = new List<Pawn>();
					garrison.RemoveAll(p => p == null || p.Destroyed);
					if (loot == null) loot = new ThingOwner<Thing>(this);
					questDestroyed.RemoveAll(r => r == null || r.defName.NullOrEmpty());
					siteFacts.RemoveAll(f => f == null);
					MigrateVgProfileIfNeeded();
				}
			}
		}

		void SanitizeDefenseByDef()
		{
			if (survivingDefenseByDef == null)
			{
				survivingDefenseByDef = new Dictionary<string, int>();
				return;
			}
			bool dirty = false;
			foreach (KeyValuePair<string, int> kv in survivingDefenseByDef)
			{
				if (kv.Key.NullOrEmpty())
				{
					dirty = true;
					break;
				}
			}
			if (!dirty) return;
			var next = new Dictionary<string, int>(survivingDefenseByDef.Count);
			foreach (KeyValuePair<string, int> kv in survivingDefenseByDef)
			{
				if (kv.Key.NullOrEmpty()) continue;
				next[kv.Key] = kv.Value;
			}
			survivingDefenseByDef = next;
		}

		void RestoreDefenseByDefIfClobbered()
		{
			bool emptied = survivingDefenseByDef == null || survivingDefenseByDef.Count == 0;
			bool hadData = defenseByDefLoadSnapshot != null && defenseByDefLoadSnapshot.Count > 0;
			if (emptied && hadData)
				survivingDefenseByDef = new Dictionary<string, int>(defenseByDefLoadSnapshot);
			else
				SanitizeDefenseByDef();
		}

		void MigrateVgProfileIfNeeded()
		{
			if (mode != StrikeAftermathMode.VirtualGarrison) return;
			if (vgSchemaVersion >= VgProfileIds.SchemaVersion && !vgProfileId.NullOrEmpty())
				return;
			VgProfile hab = VgProfile.Habitation();
			vgProfileId = hab.Id;
			vgFlags = (int)hab.Flags;
			vgSchemaVersion = VgProfileIds.SchemaVersion;
		}

		public bool HasSalvageLoot => loot != null && loot.Count > 0;
		public bool HasGarrison => garrison != null && garrison.Count > 0;

		/// <summary>
		/// Protects mapless DestroyedSettlement from orphan purge until ExpireSalvage removes the entry.
		/// Intentionally does NOT depend on loot/dialog — only mode.
		/// </summary>
		public bool IsSalvageActive => mode == StrikeAftermathMode.Salvage;
	}

	public class WorldComponent_StrikeAftermath : WorldComponent, IThingHolder
	{
		public const int SalvageTicks = 240000; // 4 days
		public const string PinHoldMap = "activemaps.aftermath.holdmap";
		/// <summary>Map-bound hibernate pin (same keep-alive as Hold; ticks skipped via MapHibernation).</summary>
		public const string PinHibernateMap = "activemaps.aftermath.hibernate";
		/// <summary>Keeps surface (+ linked pocket maps) after strike when floor entrances exist.</summary>
		public const string PinLinkedFloors = "activemaps.aftermath.linkedfloors";

		List<StrikeAftermathEntry> entries = new List<StrikeAftermathEntry>();
		/// <summary>Deferred orphan Site destroy (avoid Unload mid-explosion hitch / NRE).</summary>
		[Unsaved] List<PendingOrphanDestroy> pendingOrphans = new List<PendingOrphanDestroy>();

		struct PendingOrphanDestroy
		{
			public int siteId;
			public int readyTick;
			public string reason;
		}

		public WorldComponent_StrikeAftermath(World world) : base(world) { }

		/// <summary>Queue Site destroy a few ticks later so shell explosions finish.</summary>
		public void ScheduleOrphanSiteDestroy(Site site, string reason, int delayTicks = 120)
		{
			if (site == null || site.Destroyed) return;
			if (pendingOrphans == null)
				pendingOrphans = new List<PendingOrphanDestroy>();
			for (int i = 0; i < pendingOrphans.Count; i++)
			{
				if (pendingOrphans[i].siteId == site.ID)
					return;
			}
			pendingOrphans.Add(new PendingOrphanDestroy
			{
				siteId = site.ID,
				readyTick = Find.TickManager.TicksGame + Math.Max(1, delayTicks),
				reason = reason ?? "orphan"
			});
		}

		public IThingHolder ParentHolder => null;

		public void GetChildHolders(List<IThingHolder> outChildren)
		{
			for (int i = 0; i < entries.Count; i++)
				outChildren.Add(entries[i]);
		}

		public ThingOwner GetDirectlyHeldThings() => null;

		public static WorldComponent_StrikeAftermath Get()
			=> Find.World?.GetComponent<WorldComponent_StrikeAftermath>();

		public StrikeAftermathEntry GetByTile(PlanetTile tile)
		{
			if (!tile.Valid) return null;
			for (int i = 0; i < entries.Count; i++)
			{
				if (entries[i] != null && entries[i].tile == tile) return entries[i];
			}
			return null;
		}

		public StrikeAftermathEntry GetByWorldObject(WorldObject wo)
		{
			if (wo == null) return null;
			StrikeAftermathEntry byTile = null;
			for (int i = 0; i < entries.Count; i++)
			{
				StrikeAftermathEntry e = entries[i];
				if (e == null) continue;
				if (e.worldObjectId == wo.ID) return e;
				if (e.tile.Valid && e.tile == wo.Tile) byTile = e;
			}
			return byTile;
		}

		public bool ProtectsMaplessDestroyedSettlement(DestroyedSettlement ds)
		{
			if (ds == null) return false;
			StrikeAftermathEntry e = GetByWorldObject(ds) ?? GetByTile(ds.Tile);
			return e != null && e.IsSalvageActive;
		}

		public StrikeAftermathEntry GetOrCreate(WorldObject wo, StrikeAftermathMode mode)
		{
			StrikeAftermathEntry e = GetByWorldObject(wo) ?? GetByTile(wo.Tile);
			if (e == null)
			{
				e = new StrikeAftermathEntry();
				entries.Add(e);
			}
			e.worldObjectId = wo.ID;
			e.tile = wo.Tile;
			e.mode = mode;
			return e;
		}

		/// <summary>Bind/create entry without changing <see cref="StrikeAftermathEntry.mode"/> (or other capture state).</summary>
		public StrikeAftermathEntry EnsureSticky(WorldObject wo)
		{
			if (wo == null) return null;
			StrikeAftermathEntry e = GetByWorldObject(wo) ?? GetByTile(wo.Tile);
			if (e == null)
			{
				e = new StrikeAftermathEntry();
				entries.Add(e);
			}
			e.worldObjectId = wo.ID;
			e.tile = wo.Tile;
			return e;
		}

		/// <summary>First shell on this map parent — sticky until site/entry gone.</summary>
		public static void MarkShelled(MapParent parent)
		{
			if (parent == null || parent.Destroyed) return;
			StrikeAftermathEntry e = Get()?.EnsureSticky(parent);
			if (e != null)
				e.shellsLanded = true;
		}

		public static void MarkShelled(Map map)
		{
			if (map?.Parent != null)
				MarkShelled(map.Parent);
		}

		public static bool ShellsLanded(MapParent parent)
		{
			if (parent == null) return false;
			StrikeAftermathEntry e = Get()?.GetByWorldObject(parent) ?? Get()?.GetByTile(parent.Tile);
			return e != null && e.shellsLanded;
		}

		public void Remove(StrikeAftermathEntry e)
		{
			if (e == null) return;
			MapHibernation.ClearHibernation(e);
			e.loot?.ClearAndDestroyContents();
			e.garrison?.Clear();
			entries.Remove(e);
		}

		public void RebindTile(PlanetTile tile, WorldObject newWo)
		{
			StrikeAftermathEntry e = GetByTile(tile);
			if (e == null || newWo == null) return;
			e.worldObjectId = newWo.ID;
			e.tile = newWo.Tile;
		}

		/// <summary>After Settlement→DestroyedSettlement ID change, re-link salvage entries by tile.</summary>
		void RebindSalvageWorldObjects()
		{
			if (Find.WorldObjects == null || entries == null) return;
			for (int i = 0; i < entries.Count; i++)
			{
				StrikeAftermathEntry e = entries[i];
				if (e == null || e.mode != StrikeAftermathMode.Salvage || !e.tile.Valid) continue;
				MapParent mp = Find.WorldObjects.MapParentAt(e.tile);
				if (mp != null && mp.ID != e.worldObjectId)
					e.worldObjectId = mp.ID;
			}
		}

		public override void WorldComponentTick()
		{
			TickPendingOrphans();
			SiteFactQueue.TickEntries(entries);

			if (Find.TickManager.TicksGame % 2000 != 0) return;
			int now = Find.TickManager.TicksGame;
			for (int i = entries.Count - 1; i >= 0; i--)
			{
				StrikeAftermathEntry e = entries[i];
				if (e == null || e.mode != StrikeAftermathMode.Salvage) continue;
				if (e.expireTick < 0 || now < e.expireTick) continue;
				ExpireSalvage(e);
			}
		}

		void TickPendingOrphans()
		{
			if (pendingOrphans == null || pendingOrphans.Count == 0) return;
			int now = Find.TickManager.TicksGame;
			for (int i = pendingOrphans.Count - 1; i >= 0; i--)
			{
				PendingOrphanDestroy p = pendingOrphans[i];
				if (now < p.readyTick) continue;
				pendingOrphans.RemoveAt(i);
				Site site = FindSiteById(p.siteId);
				OrphanSiteCleanup.DestroyOrphanSiteNow(site, p.reason);
			}
		}

		static Site FindSiteById(int id)
		{
			if (Find.WorldObjects == null) return null;
			List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
			for (int i = 0; i < all.Count; i++)
			{
				if (all[i] is Site s && s.ID == id && !s.Destroyed)
					return s;
			}
			return null;
		}

		void ExpireSalvage(StrikeAftermathEntry e)
		{
			try
			{
				e.loot?.ClearAndDestroyContents();
				WorldObject wo = null;
				if (Find.WorldObjects != null)
				{
					wo = Find.WorldObjects.AllWorldObjects.FirstOrDefault(w => w.ID == e.worldObjectId);
					if (wo == null && e.tile.Valid)
						wo = Find.WorldObjects.MapParentAt(e.tile);
				}
				if (wo is DestroyedSettlement ds && !ds.HasMap && !ds.Destroyed)
					ds.Destroy();
			}
			catch (Exception ex)
			{
				Log.Warning($"[CrystallizeActiveMaps] ExpireSalvage: {ex.Message}");
			}
			Remove(e);
		}

		/// <summary>
		/// Mapless DestroyedSettlement without our salvage entry = dead vanilla orphan. Run only after
		/// this component's entries are loaded (not from ActiveMaps PostLoadInit — order race).
		/// </summary>
		public static void PurgeOrphanDestroyedSettlements()
		{
			try
			{
				if (Find.WorldObjects == null) return;
				WorldComponent_StrikeAftermath wc = Get();
				List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
				List<DestroyedSettlement> orphans = null;
				for (int i = 0; i < all.Count; i++)
				{
					if (all[i] is DestroyedSettlement ds && !ds.HasMap)
					{
						if (orphans == null) orphans = new List<DestroyedSettlement>();
						orphans.Add(ds);
					}
				}
				if (orphans == null) return;
				for (int i = 0; i < orphans.Count; i++)
				{
					DestroyedSettlement ds = orphans[i];
					if (wc != null && wc.ProtectsMaplessDestroyedSettlement(ds))
						continue;
					Log.Message($"[CrystallizeActiveMaps] Removing orphan DestroyedSettlement (no map) ID={ds.ID} tile={ds.Tile}");
					try { ds.Destroy(); }
					catch (Exception e) { Log.Warning($"[CrystallizeActiveMaps] Orphan DestroyedSettlement destroy: {e.Message}"); }
				}
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] PurgeOrphanDestroyedSettlements: {e.Message}");
			}
		}

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				if (entries == null) entries = new List<StrikeAftermathEntry>();
				RebindSalvageWorldObjects();
				MapHibernation.RecountHibernating(entries);
				// After our entries are in memory — safe vs ActiveMaps load order.
				PurgeOrphanDestroyedSettlements();
				// Second pass after every WorldComponent PostLoadInit finished.
				LongEventHandler.ExecuteWhenFinished(PurgeOrphanDestroyedSettlements);
			}
		}

		public string SalvageInspectString(WorldObject wo)
		{
			StrikeAftermathEntry e = GetByWorldObject(wo);
			if (e == null || e.mode != StrikeAftermathMode.Salvage) return null;
			if (e.expireTick < 0) return "Crystallize_AM_SalvageStash".Translate(e.loot?.Count ?? 0);
			int left = Math.Max(0, e.expireTick - Find.TickManager.TicksGame);
			return "Crystallize_AM_SalvageStashTimed".Translate(e.loot?.Count ?? 0, GenDate.ToStringTicksToPeriod(left));
		}
	}
}
