using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
    /// <summary>
    /// Settlement/Site override ShouldRemoveMapNow — patching only MapParent base misses them.
    /// Pin must block unload on every MapParent subclass.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_MapParent_KeepPinned
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var seen = new HashSet<MethodBase>();
            void Add(MethodBase m)
            {
                if (m != null) seen.Add(m);
            }

            Add(AccessTools.Method(typeof(MapParent), nameof(MapParent.ShouldRemoveMapNow)));

            Type[] types;
            try
            {
                types = typeof(MapParent).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
            }

            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t == null || t == typeof(MapParent) || !typeof(MapParent).IsAssignableFrom(t))
                    continue;
                MethodInfo declared = AccessTools.DeclaredMethod(t, nameof(MapParent.ShouldRemoveMapNow));
                Add(declared);
            }

            return seen;
        }

        public static bool Prefix(MapParent __instance, ref bool __result, ref bool alsoRemoveWorldObject)
        {
            Map map = __instance.Map;
            if (map == null) return true;

            if (ShouldKeepMap(map))
            {
                __result = false;
                alsoRemoveWorldObject = false;
                return false;
            }

            return true;
        }

        public static bool ShouldKeepMap(Map map)
        {
            if (map == null) return false;
            if (ActiveMaps.IsPinned(map)) return true;
            if (RailgunStrikeZone.IsActiveMap(map)) return true;
            MapComponent_StrikeLootWindow loot = map.GetComponent<MapComponent_StrikeLootWindow>();
            return loot != null && loot.Active;
        }
    }

    /// <summary>
    /// Settlements unload when no pawn blocks removal. Pin acts as a virtual blocker.
    /// </summary>
    [HarmonyPatch(typeof(MapPawns), "get_AnyPawnBlockingMapRemoval")]
    public static class Patch_MapPawns_KeepPinned
    {
        public static void Postfix(MapPawns __instance, ref bool __result)
        {
            if (__result) return;
            try
            {
                FieldInfo mapField = AccessTools.Field(typeof(MapPawns), "map");
                Map map = mapField?.GetValue(__instance) as Map;
                if (map != null && Patch_MapParent_KeepPinned.ShouldKeepMap(map))
                    __result = true;
            }
            catch
            {
                // ignore
            }
        }
    }
}
