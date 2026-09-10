using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Stabilize kit (plan C): full tend, safe blood-loss cap, then ensure Moving so the pawn
	/// can walk in a caravan after disband — not a full hospital heal.
	/// </summary>
	public static class OutpostHealthStabilize
	{
		const float MaxBloodLossSeverity = 0.18f;
		const float TendQuality = 1f;

		public static void StabilizeForStash(Pawn pawn)
		{
			if (pawn?.health?.hediffSet == null || pawn.Dead || pawn.Destroyed)
				return;
			if (!pawn.RaceProps.IsFlesh)
				return;

			TendAll(pawn);
			CapBloodLoss(pawn);
			EnsureCanWalk(pawn);

			pawn.health.Notify_HediffChanged(null);
		}

		static void TendAll(Pawn pawn)
		{
			List<Hediff> list = pawn.health.hediffSet.hediffs.ToList();
			int batch = 0;
			foreach (Hediff h in list)
			{
				if (h == null) continue;
				try
				{
					if (h.TendableNow(ignoreTimer: true))
						h.Tended(TendQuality, TendQuality, batch++);
				}
				catch
				{
					/* ignore exotic hediffs */
				}
			}
		}

		static void CapBloodLoss(Pawn pawn)
		{
			Hediff blood = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss);
			if (blood != null && blood.Severity > MaxBloodLossSeverity)
				blood.Severity = MaxBloodLossSeverity;
		}

		static void EnsureCanWalk(Pawn pawn)
		{
			int guard = 0;
			while (pawn.Downed && guard++ < 8)
			{
				if (pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
					break;

				List<Hediff_Injury> injuries = pawn.health.hediffSet.hediffs
					.OfType<Hediff_Injury>()
					.Where(i => i != null && !i.IsPermanent() && i.Severity > 0.05f)
					.OrderByDescending(i => i.Severity)
					.ToList();
				if (injuries.Count == 0)
					break;

				Hediff_Injury worst = injuries[0];
				float heal = Mathf.Max(0.5f, worst.Severity * 0.35f);
				worst.Heal(heal);
				TendAll(pawn);
				CapBloodLoss(pawn);
			}

			if (pawn.Downed)
			{
				TendAll(pawn);
				CapBloodLoss(pawn);
			}
		}
	}
}
