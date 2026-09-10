using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Crystallize.ActiveMaps
{
    public class ActiveMapsMod : Mod
    {
        public static ActiveMapsSettings Settings;

        public ActiveMapsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<ActiveMapsSettings>();
            var harmony = new Harmony("crystallize.activemaps");
            int ok = 0, fail = 0;
            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
            {
                object[] attrs = type.GetCustomAttributes(typeof(HarmonyPatch), inherit: false);
                if (attrs == null || attrs.Length == 0) continue;
                try
                {
                    new PatchClassProcessor(harmony, type).Patch();
                    ok++;
                }
                catch (Exception e)
                {
                    fail++;
                    Log.Warning($"[CrystallizeActiveMaps] Patch failed on {type.Name}: {e.Message}");
                }
            }
            Log.Message($"[CrystallizeActiveMaps] Harmony patches applied: {ok} ok, {fail} failed.");
        }

        public override string SettingsCategory()
            => "Crystallize_AM_SettingsCategory".Translate();

        public override void DoSettingsWindowContents(Rect inRect)
        {
            ActiveMapsSettingsUtil.Draw(inRect, Settings ?? GetSettings<ActiveMapsSettings>());
            base.DoSettingsWindowContents(inRect);
        }
    }

    public enum ActiveMapReason
    {
        None = 0,
        WorldTargeting = 1,
        FireMission = 2,
        ProjectileInFlight = 3,
        LootWindow = 4,
        Other = 8
    }
}
