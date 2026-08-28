using BepInEx;

namespace HHMods.QoL
{
    [BepInPlugin(PluginId, "HH Quality of Life", "0.1.0")]
    [BepInDependency(HHMods.Core.Plugin.PluginId)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.hh.qol";

        private void Awake()
        {
            Logger.LogInfo($"{PluginId} loaded (stub)");
        }
    }
}
