using BepInEx;

namespace HHMods.Vehicles
{
    [BepInPlugin(PluginId, "HH Vehicle Mods", "0.1.0")]
    [BepInDependency(HHMods.Core.Plugin.PluginId)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.hh.vehicles";

        private void Awake()
        {
            Logger.LogInfo($"{PluginId} loaded (stub)");
        }
    }
}
