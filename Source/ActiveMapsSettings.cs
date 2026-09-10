using System;
using UnityEngine;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>Map-bound Preserve policy (plan C3 / settings-fragile-holdmap).</summary>
	public enum FragileMapPolicy : byte
	{
		/// <summary>Pin + skip map/thing ticks (default).</summary>
		Hibernate = 0,
		/// <summary>Pin + full tick (old Hold).</summary>
		Hold = 1,
		/// <summary>Hibernate on Peek End, but refuse starting a strike on the tile.</summary>
		ForbidStrike = 2
	}

	public class ActiveMapsSettings : ModSettings
	{
		public FragileMapPolicy fragileMapPolicy = FragileMapPolicy.Hibernate;

		public override void ExposeData()
		{
			Scribe_Values.Look(ref fragileMapPolicy, "fragileMapPolicy", FragileMapPolicy.Hibernate);
		}

		public bool PreferHibernate
			=> fragileMapPolicy == FragileMapPolicy.Hibernate
			   || fragileMapPolicy == FragileMapPolicy.ForbidStrike;

		public bool ForbidStrikeOnFragile
			=> fragileMapPolicy == FragileMapPolicy.ForbidStrike;
	}

	public static class ActiveMapsSettingsUtil
	{
		public static ActiveMapsSettings Get()
			=> LoadedModManager.GetMod<ActiveMapsMod>()?.GetSettings<ActiveMapsSettings>()
			   ?? new ActiveMapsSettings();

		public static void Draw(Rect inRect, ActiveMapsSettings s)
		{
			if (s == null) return;
			Listing_Standard list = new Listing_Standard();
			list.Begin(inRect);
			list.Label("Crystallize_AM_Settings_FragileHeader".Translate());
			list.Gap(6f);

			if (list.RadioButton("Crystallize_AM_Settings_FragileHibernate".Translate(),
				    s.fragileMapPolicy == FragileMapPolicy.Hibernate))
				s.fragileMapPolicy = FragileMapPolicy.Hibernate;
			list.Label("Crystallize_AM_Settings_FragileHibernateTip".Translate());
			list.Gap(4f);

			if (list.RadioButton("Crystallize_AM_Settings_FragileHold".Translate(),
				    s.fragileMapPolicy == FragileMapPolicy.Hold))
				s.fragileMapPolicy = FragileMapPolicy.Hold;
			list.Label("Crystallize_AM_Settings_FragileHoldTip".Translate());
			list.Gap(4f);

			if (list.RadioButton("Crystallize_AM_Settings_FragileForbid".Translate(),
				    s.fragileMapPolicy == FragileMapPolicy.ForbidStrike))
				s.fragileMapPolicy = FragileMapPolicy.ForbidStrike;
			list.Label("Crystallize_AM_Settings_FragileForbidTip".Translate());

			list.End();
		}
	}
}
