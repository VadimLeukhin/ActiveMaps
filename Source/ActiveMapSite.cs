using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Crystallize.ActiveMaps
{
    public class ActiveMapSite : MapParent
    {
        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = false;
            Map map = Map;
            if (map == null) return false;

            if (ActiveMaps.IsPinned(map))
                return false;

            MapComponent_StrikeLootWindow loot = map.GetComponent<MapComponent_StrikeLootWindow>();
            if (loot != null && loot.Active)
                return false;

            alsoRemoveWorldObject = true;
            return true;
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            Map map = Map;
            MapComponent_StrikeLootWindow loot = map?.GetComponent<MapComponent_StrikeLootWindow>();
            if (loot != null && loot.Active)
            {
                if (!text.NullOrEmpty()) text += "\n";
                text += loot.WaitingForPlayerLeave
                    ? "Crystallize_AM_LootWindowWaitForLeaveInspect".Translate()
                    : "Crystallize_AM_LootWindow".Translate(GenDate.ToStringTicksToPeriod(loot.TicksLeft));
            }
            return text;
        }
    }
}
