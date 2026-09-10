using System;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Remote strike shells often explode with no player Thing as instigator —
	/// vanilla Notify_BuildingTookDamage / MemberTookDamage never runs, so
	/// allies and neutrals stay friendly. Force AttackedSettlement on first shot.
	/// </summary>
	public static class StrikeHostilityUtility
	{
		static int notifiedWorldObjectId = -1;

		public static void ClearSession()
		{
			notifiedWorldObjectId = -1;
		}

		public static void NotifyPlayerAttackedMap(Map map)
		{
			NotifyPlayerAttackedParent(map?.Parent);
		}

		public static void NotifyPlayerAttackedParent(MapParent parent)
		{
			if (parent == null || parent.Destroyed) return;
			Faction fac = parent.Faction;
			if (fac == null || fac.IsPlayer || fac.defeated || fac.temporary) return;
			if (fac.HostileTo(Faction.OfPlayer)) return;

			// One letter / goodwill swing per target WO for this strike session.
			if (notifiedWorldObjectId == parent.ID) return;
			notifiedWorldObjectId = parent.ID;

			try
			{
				int change = Faction.OfPlayer.GoodwillToMakeHostile(fac);
				if (change >= 0) return;
				Faction.OfPlayer.TryAffectGoodwillWith(
					fac,
					change,
					canSendMessage: true,
					canSendHostilityLetter: true,
					reason: HistoryEventDefOf.AttackedSettlement,
					lookTarget: parent);
			}
			catch (Exception e)
			{
				Log.Warning($"[CrystallizeActiveMaps] Strike hostility: {e.Message}");
			}
		}

		/// <summary>Any free colonist so GenExplosion DamageInfo has Faction.OfPlayer.</summary>
		public static Thing TryPlayerInstigator()
		{
			if (Find.Maps == null) return null;
			for (int i = 0; i < Find.Maps.Count; i++)
			{
				Map map = Find.Maps[i];
				var colonists = map?.mapPawns?.FreeColonistsSpawned;
				if (colonists != null && colonists.Count > 0)
					return colonists[0];
			}
			return null;
		}
	}
}
