using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Crystallize.ActiveMaps
{
    [StaticConstructorOnStartup]
    public static class FireMissionUi
    {
        private static readonly Texture2D cachedTargetIcon;
        private static readonly Texture2D cachedStopFireIcon;

        /// <summary>Reused across GUI frames — MapInterfaceOnGUI runs every render frame.</summary>
        static readonly List<Gizmo> cachedStrikeGizmos = new List<Gizmo>(8);
        static int cachedFingerprint = int.MinValue;

        static FireMissionUi()
        {
            try
            {
                cachedTargetIcon = ContentFinder<Texture2D>.Get("Rimatomics/UI/FireMission", reportFailure: false)
                    ?? ContentFinder<Texture2D>.Get("UI/Commands/Attack", reportFailure: false)
                    ?? TexCommand.Attack;
            }
            catch
            {
                cachedTargetIcon = TexCommand.Attack;
            }
            try
            {
                cachedStopFireIcon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel", reportFailure: false)
                    ?? ContentFinder<Texture2D>.Get("UI/Commands/Halt", reportFailure: false)
                    ?? TexCommand.ClearPrioritizedWork;
            }
            catch
            {
                cachedStopFireIcon = TexCommand.ClearPrioritizedWork;
            }
        }

        public static Texture2D TargetIcon => cachedTargetIcon ?? TexCommand.Attack;
        public static Texture2D StopFireIcon => cachedStopFireIcon ?? TexCommand.ClearPrioritizedWork;

        public static void InvalidateStrikeGizmoCache()
        {
            cachedFingerprint = int.MinValue;
        }

        static int ComputeFingerprint(Map map)
        {
            unchecked
            {
                int fp = map != null ? map.uniqueID : -1;
                fp = (fp * 397) ^ (OutpostStrikeZone.Active ? 1 : 0);
                fp = (fp * 397) ^ (RailgunStrikeZone.Active ? 2 : 0);
                // NeedsEndControl is tick-throttled; include so gizmos refresh when scan result flips.
                fp = (fp * 397) ^ (RailgunStrikeZone.NeedsEndControl() ? 4 : 0);
                return fp;
            }
        }

        /// <summary>Stable gizmo list for the current strike state — no per-frame alloc.</summary>
        public static List<Gizmo> GetStrikeMapGizmosCached(Map map)
        {
            int fp = ComputeFingerprint(map);
            if (fp == cachedFingerprint)
                return cachedStrikeGizmos;

            cachedFingerprint = fp;
            cachedStrikeGizmos.Clear();
            RebuildStrikeGizmos(cachedStrikeGizmos);
            return cachedStrikeGizmos;
        }

        static void RebuildStrikeGizmos(List<Gizmo> dst)
        {
            if (OutpostStrikeZone.NeedsControls())
            {
                dst.Add(new Command_Action
                {
                    defaultLabel = "Crystallize_AM_ChangeTarget".Translate(),
                    defaultDesc = "Crystallize_AM_ChangeTargetDesc".Translate(),
                    icon = TargetIcon,
                    action = OutpostStrikeZone.BeginLocalRetarget
                });
                dst.Add(new Command_Action
                {
                    defaultLabel = "Crystallize_AM_StopFire".Translate(),
                    defaultDesc = "Crystallize_AM_StopFireDesc".Translate(),
                    icon = StopFireIcon,
                    action = OutpostStrikeZone.StopFire
                });
                dst.Add(new Command_Action
                {
                    defaultLabel = "Crystallize_AM_EndStrike".Translate(),
                    defaultDesc = "Crystallize_AM_EndStrikeDesc".Translate(),
                    icon = TexCommand.ClearPrioritizedWork,
                    action = () => OutpostStrikeZone.EndMission(jumpHome: true)
                });
            }

            if (!RailgunStrikeZone.NeedsEndControl())
                return;

            if (RailgunStrikeZone.Active)
            {
                dst.Add(new Command_Action
                {
                    defaultLabel = "Crystallize_AM_ChangeTarget".Translate(),
                    defaultDesc = "Crystallize_AM_ChangeTargetDesc".Translate(),
                    icon = TargetIcon,
                    action = RailgunStrikeZone.BeginLocalRetarget
                });
                dst.Add(new Command_Action
                {
                    defaultLabel = "Crystallize_AM_StopFire".Translate(),
                    defaultDesc = "Crystallize_AM_StopFireDesc".Translate(),
                    icon = StopFireIcon,
                    action = RailgunStrikeZone.StopFire
                });
            }

            dst.Add(new Command_Action
            {
                defaultLabel = "Crystallize_AM_EndStrike".Translate(),
                defaultDesc = "Crystallize_AM_EndStrikeDesc".Translate(),
                icon = TexCommand.ClearPrioritizedWork,
                action = () => RailgunStrikeZone.EndZone(RailgunStrikeEndReason.Manual)
            });
        }
    }

    [HarmonyPatch(typeof(MapInterface), "MapInterfaceOnGUI_BeforeMainTabs")]
    public static class Patch_MapInterface_StrikeGizmos
    {
        public static void Postfix()
        {
            try
            {
                if (Current.ProgramState != ProgramState.Playing) return;
                Map map = Find.CurrentMap;
                if (map == null) return;
                if (!RailgunStrikeZone.IsActiveMap(map) && !OutpostStrikeZone.IsActiveMap(map)) return;
                if (Find.Selector.NumSelected > 0) return;

                List<Gizmo> gizmos = FireMissionUi.GetStrikeMapGizmosCached(map);
                if (gizmos.Count == 0) return;

                Gizmo mouseover;
                GizmoGridDrawer.DrawGizmoGrid(
                    gizmos,
                    GizmoGridDrawer.HeightDrawnRecently,
                    out mouseover,
                    null,
                    null,
                    null,
                    true);
            }
            catch (Exception e)
            {
                Log.Warning($"[CrystallizeActiveMaps] Strike map gizmos GUI failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGrid))]
    public static class Patch_GizmoGrid_AppendStrike
    {
        static readonly List<Gizmo> mergeBuffer = new List<Gizmo>(32);

        public static void Prefix(ref IEnumerable<Gizmo> gizmos)
        {
            try
            {
                Map map = Find.CurrentMap;
                if (!RailgunStrikeZone.IsActiveMap(map) && !OutpostStrikeZone.IsActiveMap(map))
                    return;
                if (Find.Selector.NumSelected <= 0) return;
                if (gizmos == null) return;

                List<Gizmo> strike = FireMissionUi.GetStrikeMapGizmosCached(map);
                if (strike.Count == 0) return;

                mergeBuffer.Clear();
                foreach (Gizmo g in gizmos)
                {
                    if (g != null) mergeBuffer.Add(g);
                }
                for (int i = 0; i < strike.Count; i++)
                    mergeBuffer.Add(strike[i]);
                gizmos = mergeBuffer;
            }
            catch
            {
                // ignore
            }
        }
    }
}
