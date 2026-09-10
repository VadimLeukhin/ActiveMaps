using System;
using HarmonyLib;
using Verse;

namespace Crystallize.ActiveMaps
{
	/// <summary>
	/// VG apply punches holes in freshly generated walls/turrets. Vanilla then fires
	/// "Area revealed" / «Открыта область» once per fog-blocker removal — spam on re-enter.
	/// </summary>
	public static class FogRevealSuppress
	{
		static int depth;

		public static bool Active => depth > 0;

		public static void Push() => depth++;

		public static void Pop()
		{
			if (depth > 0) depth--;
		}

		public static IDisposable Scope() => new ScopeToken();

		sealed class ScopeToken : IDisposable
		{
			public ScopeToken() => Push();
			public void Dispose() => Pop();
		}
	}

	[HarmonyPatch(typeof(FogGrid), "NotifyAreaRevealed")]
	public static class Patch_FogGrid_SuppressAreaRevealed
	{
		static bool Prefix() => !FogRevealSuppress.Active;
	}
}
