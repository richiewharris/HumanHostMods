using HarmonyLib;

namespace HHMods.Minimap
{
    /// <summary>
    /// Diagnostic: log when the game opens/closes its world map, and what state its
    /// CompassPro is in. Helps us understand why the world map might be black.
    /// </summary>
    [HarmonyPatch(typeof(global::World_Map_Mgr), nameof(global::World_Map_Mgr.Enable_Disable_World_Map))]
    internal static class WorldMapOpenPatch
    {
        [HarmonyPrefix]
        private static void Prefix(global::World_Map_Mgr __instance)
        {
            try
            {
                var cp = __instance._CompassPro;
                Plugin.Log.LogInfo(
                    "[WorldMapOpenPatch] Enable_Disable_World_Map called.\n"
                    + $"    game CompassPro: {(cp != null ? cp.gameObject.name : "(null)")}\n"
                    + $"    showMiniMap={(cp != null ? cp.showMiniMap : false)}\n"
                    + $"    miniMapZoomState={(cp != null ? cp.miniMapZoomState : false)}\n"
                    + $"    miniMapAlpha={(cp != null ? cp.miniMapAlpha : 0f)}");

                if (cp != null)
                {
                    var uiRoot = HarmonyLib.AccessTools.Field(typeof(CompassNavigatorPro.CompassPro), "miniMapUIRoot")?.GetValue(cp) as UnityEngine.Transform;
                    if (uiRoot != null)
                    {
                        Plugin.Log.LogInfo($"    game miniMapUIRoot: {uiRoot.name}   active={uiRoot.gameObject.activeInHierarchy}");
                        foreach (var img in uiRoot.GetComponentsInChildren<UnityEngine.UI.Image>(includeInactive: true))
                        {
                            var name = img.gameObject.name;
                            if (name == "Black_BG" || name == "Image_BG" || (img.sprite != null && img.sprite.name == "World_Map_Black"))
                            {
                                Plugin.Log.LogInfo($"    game UI: {name}  enabled={img.enabled}  sprite={(img.sprite != null ? img.sprite.name : "(null)")}  active={img.gameObject.activeInHierarchy}");
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[WorldMapOpenPatch] threw: {e}"); }
        }
    }
}
