using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HHMods.Core;
using UnityEngine;

namespace HHMods.Minimap
{
    [BepInPlugin(PluginId, "HH Minimap", "0.1.0")]
    [BepInDependency(HHMods.Core.Plugin.PluginId)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.hh.minimap";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        // ---- Config ----
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<bool> ShowOnStart;
        internal static ConfigEntry<float> VisibleRadius;
        internal static ConfigEntry<float> Size;
        internal static ConfigEntry<Vector2> ScreenAnchor01;   // 0..1 fraction of screen
        internal static ConfigEntry<bool> KeepMapStraight;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ToggleKey = Config.Bind("Hotkeys", "Toggle Minimap",
                new KeyboardShortcut(KeyCode.N),
                "Show / hide the minimap. Default N — avoid Shift+M which the game uses for the world map.");
            ShowOnStart = Config.Bind("Display", "Show On Start", false,
                "If true, the minimap and filter panel are visible as soon as the game world loads. "
              + "Default false while we validate — press N in-game to enable.");
            VisibleRadius = Config.Bind("Display", "Visible Radius", 150f,
                "World-space radius of POIs shown on the minimap (in meters).");
            Size = Config.Bind("Display", "Panel Size", 260f,
                "Minimap panel size in screen pixels.");
            ScreenAnchor01 = Config.Bind("Display", "Screen Anchor (0-1)", new Vector2(0.98f, 0.98f),
                "Anchor fraction — (1,1) = top-right, (0,0) = bottom-left.");
            KeepMapStraight = Config.Bind("Display", "Keep Map Straight (North Up)", false,
                "If true, map is always north-up and the player arrow rotates. "
              + "If false (default), the map rotates with the player and the arrow stays pointing up.");

            // Register Harmony patches so we bind to CompassPro the instant World_Map_Mgr._Start fires
            _harmony = new Harmony(PluginId);
            try
            {
                _harmony.PatchAll(typeof(Plugin).Assembly);
                Log.LogInfo($"[Minimap] Harmony patched — {_harmony.GetPatchedMethods()?.ToString()}");
            }
            catch (System.Exception e)
            {
                Log.LogError($"[Minimap] Harmony patch failed: {e}");
            }

            // Layer + controller are wired once the game world exists
            GameEvents.HubReady += OnHubReady;

            Log.LogInfo($"{PluginId} loaded — awaiting HubReady");
        }

        private void OnHubReady()
        {
            var host = new GameObject("HHMods.Minimap.Controller");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<MinimapController>();
            host.AddComponent<HotkeyBinding>();
            var panel = host.AddComponent<FilterPanel>();
            panel.SetVisible(ShowOnStart.Value);

            BuiltinLayers.RegisterAll();

            Log.LogInfo("[Minimap] controller spawned, built-in layers registered");
        }
    }
}
