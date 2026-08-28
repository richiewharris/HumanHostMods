using BepInEx;

namespace HHMods.Weapons
{
    [BepInPlugin(PluginId, "HH Weapons", "0.1.0")]
    [BepInDependency(HHMods.Core.Plugin.PluginId)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.hh.weapons";

        private void Awake()
        {
            Logger.LogInfo($"{PluginId} loaded (stub)");
        }
    }
}
