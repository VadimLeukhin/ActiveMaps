using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Salvage stash → caravan transfer. Same TransferableOneWayWidget pattern as
	/// FormCaravan / LoadTransporters (per-stack counts, mass, market value).
	/// </summary>
	public class Dialog_StrikeSalvageTransfer : Window
	{
		readonly Caravan caravan;
		readonly StrikeAftermathEntry entry;
		readonly PlanetTile siteTile;
		readonly List<TransferableOneWay> transferables = new List<TransferableOneWay>();
		TransferableOneWayWidget transferWidget;

		public override Vector2 InitialSize => new Vector2(860f, 640f);

		public Dialog_StrikeSalvageTransfer(Caravan caravan, StrikeAftermathEntry entry, PlanetTile siteTile)
		{
			this.caravan = caravan;
			this.entry = entry;
			this.siteTile = siteTile;
			forcePause = true;
			absorbInputAroundWindow = true;
			closeOnClickedOutside = false;
			BuildTransferables();
			BuildWidget();
		}

		void BuildTransferables()
		{
			transferables.Clear();
			if (entry?.loot == null) return;
			foreach (Thing t in entry.loot)
			{
				if (t == null || t.Destroyed) continue;
				TransferableOneWay existing = TransferableUtility.TransferableMatchingDesperate(
					t, transferables, TransferAsOneMode.PodsOrCaravanPacking);
				if (existing == null)
				{
					existing = new TransferableOneWay();
					transferables.Add(existing);
				}
				existing.things.Add(t);
			}
		}

		void BuildWidget()
		{
			transferWidget = new TransferableOneWayWidget(
				transferables,
				"Crystallize_AM_SalvageSource".Translate(),
				"Crystallize_AM_SalvageDest".Translate(),
				"Crystallize_AM_SalvageSourceCount".Translate(),
				drawMass: true,
				ignorePawnInventoryMass: IgnorePawnsInventoryMode.Ignore,
				includePawnsMassInMassUsage: false,
				availableMassGetter: () => caravan != null
					? caravan.MassCapacity - caravan.MassUsage
					: float.MaxValue,
				extraHeaderSpace: 0f,
				ignoreSpawnedCorpseGearAndInventoryMass: true,
				tile: caravan?.Tile,
				drawMarketValue: true,
				drawEquippedWeapon: false,
				drawNutritionEatenPerDay: false,
				drawMechEnergy: false,
				drawItemNutrition: true,
				drawForagedFoodPerDay: false,
				drawDaysUntilRot: true,
				playerPawnsReadOnly: false,
				drawIdeo: false,
				drawXenotype: false);
		}

		public override void DoWindowContents(Rect inRect)
		{
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "Crystallize_AM_SalvageTransferTitle".Translate());
			Text.Font = GameFont.Small;
			Widgets.Label(new Rect(0f, 34f, inRect.width, 28f), "Crystallize_AM_SalvageTransferTip".Translate());

			Rect list = new Rect(0f, 68f, inRect.width, inRect.height - 120f);
			bool scroll = true;
			transferWidget?.OnGUI(list, out scroll);

			Rect buttons = new Rect(0f, inRect.height - 40f, inRect.width, 35f);
			float third = buttons.width / 3f;
			if (Widgets.ButtonText(new Rect(buttons.x, buttons.y, third - 4f, buttons.height), "CancelButton".Translate()))
				Close();
			if (Widgets.ButtonText(new Rect(buttons.x + third, buttons.y, third - 4f, buttons.height), "Crystallize_AM_SalvageTakeAll".Translate()))
				SelectAll();
			if (Widgets.ButtonText(new Rect(buttons.x + third * 2f, buttons.y, third - 4f, buttons.height), "Crystallize_AM_SalvageAccept".Translate()))
			{
				if (TryAccept())
					Close();
			}
		}

		void SelectAll()
		{
			for (int i = 0; i < transferables.Count; i++)
			{
				TransferableOneWay t = transferables[i];
				if (t == null) continue;
				t.AdjustTo(t.GetMaximumToTransfer());
			}
			SoundDefOf.Click.PlayOneShotOnCamera();
		}

		bool TryAccept()
		{
			if (caravan == null || entry?.loot == null) return false;
			if (!StrikeSalvageUtility.CaravanCanTakeSalvageLoot(caravan, siteTile))
			{
				Messages.Message("Crystallize_AM_SalvageMustVisit".Translate(), MessageTypeDefOf.RejectInput, false);
				return false;
			}

			int stacksTaken = 0;
			for (int i = 0; i < transferables.Count; i++)
			{
				TransferableOneWay tr = transferables[i];
				if (tr == null || tr.CountToTransfer <= 0) continue;
				TransferableUtility.TransferNoSplit(
					tr.things,
					tr.CountToTransfer,
					(Thing thing, int count) =>
					{
						Thing taken = entry.loot.Take(thing, count);
						if (taken == null) return;
						CaravanInventoryUtility.GiveThing(caravan, taken);
						stacksTaken++;
					},
					removeIfTakingEntireThing: true,
					errorIfNotEnoughThings: false);
			}

			if (stacksTaken <= 0)
			{
				Messages.Message("Crystallize_AM_SalvageNothingSelected".Translate(), MessageTypeDefOf.RejectInput, false);
				return false;
			}

			if (entry.loot.Count == 0)
			{
				Messages.Message("Crystallize_AM_SalvageEmptied".Translate(), MessageTypeDefOf.TaskCompletion, false);
				WorldComponent_StrikeAftermath.Get()?.Remove(entry);
			}
			else
			{
				Messages.Message(
					"Crystallize_AM_SalvagePartialTaken".Translate(entry.loot.Count),
					MessageTypeDefOf.TaskCompletion,
					false);
			}
			SoundDefOf.Click.PlayOneShotOnCamera();
			return true;
		}
	}
}
