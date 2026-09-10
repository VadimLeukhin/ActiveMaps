using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// Habitation → full VG; PreserveSite → QuestVgLight (Hibernate/Hold never reach Capture).
	/// </summary>
	public static class VgProfileResolver
	{
		public static bool TryResolveForCapture(MapParent parent, out VgProfile profile)
		{
			profile = default;
			if (parent == null) return false;
			StrikeMapClass cls = StrikeMapClassifier.Classify(parent);
			switch (cls)
			{
				case StrikeMapClass.Habitation:
					profile = VgProfile.Habitation();
					return true;
				case StrikeMapClass.PreserveSite:
					profile = VgProfile.QuestVgLight();
					return true;
				default:
					return false;
			}
		}

		/// <summary>Profile baked into entry at capture (apply must not re-Classify).</summary>
		public static VgProfile ResolveFromEntry(StrikeAftermathEntry entry)
		{
			if (entry == null) return VgProfile.Habitation();
			if (entry.mode != StrikeAftermathMode.VirtualGarrison)
				return VgProfile.EvacuateOnly();
			return VgProfile.FromSaved(entry.vgProfileId, entry.vgFlags);
		}

		public static void StampEntry(StrikeAftermathEntry entry, VgProfile profile)
		{
			if (entry == null) return;
			entry.vgSchemaVersion = VgProfileIds.SchemaVersion;
			entry.vgProfileId = profile.Id;
			entry.vgFlags = (int)profile.Flags;
		}
	}
}
