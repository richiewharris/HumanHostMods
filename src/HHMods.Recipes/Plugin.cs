using BepInEx;

namespace HHMods.Recipes
{
    [BepInPlugin(PluginId, "HH Recipes", "0.1.0")]
    [BepInDependency(HHMods.Core.Plugin.PluginId)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.hh.recipes";

        private void Awake()
        {
            Logger.LogInfo($"{PluginId} loaded (stub)");
        }
    }
}
