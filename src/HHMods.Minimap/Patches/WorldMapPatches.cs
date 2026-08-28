using HarmonyLib;

namespace HHMods.Minimap
{
    /// <summary>
    /// Deterministic binding: hook World_Map_Mgr._Start so we can grab its populated
    /// _CompassPro reference the instant the game finishes initializing the world map.
    /// Beats polling FindObjectOfType.
    /// </summary>
    [HarmonyPatch(typeof(global::World_Map_Mgr), nameof(global::World_Map_Mgr._Start))]
    internal static class WorldMapPatches_Start
    {
        [HarmonyPostfix]
        private static void Postfix(global::World_Map_Mgr __instance)
        {
            var ctrl = MinimapController.Instance;
            if (ctrl == null)
            {
                Plugin.Log.LogWarning("[WorldMapPatches] World_Map_Mgr._Start fired before controller existed — polling will still catch it.");
                return;
            }
            ctrl.OnWorldMapMgrStarted(__instance);
        }
    }
}
