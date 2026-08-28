using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace HHMods.Core
{
    [BepInPlugin(PluginId, "HH Mods Core", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.hh.core";

        internal static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            var host = new GameObject("HHMods.Core.Host");
            Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<Scheduler>();

            Log.LogInfo($"{PluginId} loaded");
        }
    }
}
