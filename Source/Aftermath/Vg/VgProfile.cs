using System;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>How VG capture/apply behaves. Stored on entry at capture time.</summary>
	public static class VgProfileIds
	{
		public const string Habitation = "Habitation";
		public const string EvacuateOnly = "EvacuateOnly";
		public const string QuestVgLight = "QuestVgLight";

		public const int SchemaVersion = 1;
	}

	[Flags]
	public enum VgFlags
	{
		None = 0,
		FoldDefenders = 1 << 0,
		CullRegenHostiles = 1 << 1,
		DefenseLedger = 1 << 2,
		StructureSketch = 1 << 3,
		LordDefendBase = 1 << 4,
		EvacuateQuestSignificant = 1 << 5
	}

	public readonly struct VgProfile
	{
		public readonly string Id;
		public readonly VgFlags Flags;

		public VgProfile(string id, VgFlags flags)
		{
			Id = id ?? VgProfileIds.Habitation;
			Flags = flags;
		}

		public bool Has(VgFlags flag) => (Flags & flag) != 0;

		public static VgProfile Habitation() => new VgProfile(
			VgProfileIds.Habitation,
			VgFlags.FoldDefenders
			| VgFlags.CullRegenHostiles
			| VgFlags.DefenseLedger
			| VgFlags.StructureSketch
			| VgFlags.LordDefendBase);

		public static VgProfile EvacuateOnly() => new VgProfile(
			VgProfileIds.EvacuateOnly,
			VgFlags.EvacuateQuestSignificant);

		/// <summary>Preserve after shells: fold+ledger+cull+cover sketch (walls/sandbags voids), evacuate quest things.</summary>
		public static VgProfile QuestVgLight() => new VgProfile(
			VgProfileIds.QuestVgLight,
			VgFlags.FoldDefenders
			| VgFlags.CullRegenHostiles
			| VgFlags.DefenseLedger
			| VgFlags.StructureSketch
			| VgFlags.EvacuateQuestSignificant);

		public static VgProfile FromSaved(string profileId, int flags)
		{
			if (profileId.NullOrEmpty())
				return Habitation();
			if (string.Equals(profileId, VgProfileIds.EvacuateOnly, StringComparison.OrdinalIgnoreCase))
				return EvacuateOnly();
			if (string.Equals(profileId, VgProfileIds.QuestVgLight, StringComparison.OrdinalIgnoreCase))
				return QuestVgLight();
			if (flags != 0)
				return new VgProfile(profileId, (VgFlags)flags);
			return Habitation();
		}
	}
}
