using HarmonyLib;

namespace HHMods.Minimap
{
    /// <summary>
    /// CompassPro polls Input.mouseScrollDelta in its own Update and calls MiniMapZoomIn/Out.
    /// The game already uses scroll for hotbar switching, so we no-op these so CompassPro
    /// doesn't hijack the scroll wheel.
    /// </summary>
    [HarmonyPatch(typeof(CompassNavigatorPro.CompassPro), "MiniMapZoomIn")]
    internal static class ZoomPatches_In
    {
        [HarmonyPrefix]
        private static bool Prefix() => false; // false → skip original method
    }

    [HarmonyPatch(typeof(CompassNavigatorPro.CompassPro), "MiniMapZoomOut")]
    internal static class ZoomPatches_Out
    {
        [HarmonyPrefix]
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(CompassNavigatorPro.CompassPro), "MiniMapZoomToggle")]
    internal static class ZoomPatches_Toggle
    {
        [HarmonyPrefix]
        private static bool Prefix() => false;
    }
}
