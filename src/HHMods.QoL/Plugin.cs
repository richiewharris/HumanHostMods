using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using HHMods.QoL.ChatBox;
using UnityEngine;

namespace HHMods.QoL
{
    [BepInPlugin(PluginId, "HH Quality of Life", "0.1.0")]
    [BepInDependency(HHMods.Core.Plugin.PluginId)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.hh.qol";

        // ---- ChatBox display ----
        internal static ConfigEntry<bool> ChatBoxEnabled;
        internal static ConfigEntry<float> ChatBoxWidth;
        internal static ConfigEntry<int> ChatBoxMaxRows;
        internal static ConfigEntry<Vector2> ChatBoxAnchor01;
        internal static ConfigEntry<float> ChatBoxMarginX;
        internal static ConfigEntry<float> ChatBoxMarginY;
        internal static ConfigEntry<int> ChatBoxFontSize;

        // ---- Tag filters (persistent, one per tag) ----
        internal static ConfigEntry<bool> ShowInfo;
        internal static ConfigEntry<bool> ShowPickup;
        internal static ConfigEntry<bool> ShowWarning;
        internal static ConfigEntry<bool> ShowBroadcast;

        // ---- Suppression of game UI ----
        internal static ConfigEntry<bool> SuppressGameNotifBubbles;
        internal static ConfigEntry<bool> SuppressGameHordeBanner;

        private Harmony _harmony;

        private void Awake()
        {
            ChatBoxEnabled = Config.Bind("ChatBox", "Enabled", true,
                "Show the notification log panel.");
            ChatBoxWidth = Config.Bind("ChatBox", "Width (px)", 420f,
                new ConfigDescription("Panel width in screen pixels.", new AcceptableValueRange<float>(200f, 900f)));
            ChatBoxMaxRows = Config.Bind("ChatBox", "Max Rows", 10,
                new ConfigDescription("How many message rows to show. Older messages persist in the buffer (up to 200) even when scrolled off.",
                    new AcceptableValueRange<int>(1, 30)));
            ChatBoxAnchor01 = Config.Bind("ChatBox", "Screen Anchor (0-1)", new Vector2(0f, 0f),
                "Anchor fraction — (0,0) = bottom-left (default, above the health bar), (1,0) = bottom-right, (0,1) = top-left, (1,1) = top-right.");
            ChatBoxMarginX = Config.Bind("ChatBox", "Screen Margin X (px)", 8f,
                new ConfigDescription("Horizontal margin from the anchor edge.", new AcceptableValueRange<float>(0f, 400f)));
            ChatBoxMarginY = Config.Bind("ChatBox", "Screen Margin Y (px)", 60f,
                new ConfigDescription("Vertical margin from the anchor edge. Default 60px puts the bottom of the ChatBox just above the health/thirst readout on the left.",
                    new AcceptableValueRange<float>(0f, 400f)));
            ChatBoxFontSize = Config.Bind("ChatBox", "Font Size (px)", 14,
                new ConfigDescription("Font size for message rows. Also selectable in-game via the size picker at the top-right of the panel. Only affects message text, not filter chips.",
                    new AcceptableValueRange<int>(10, 24)));

            // One-shot migration: the ChatBox default moved from bottom-right to bottom-left
            // in this build. If the config still has the old right-side default, migrate to
            // the new position so returning users don't have to hand-edit their config file.
            // We only migrate the exact old-default combination — any user-modified position
            // is left alone.
            if (ChatBoxAnchor01.Value == new Vector2(1f, 0f) && Mathf.Approximately(ChatBoxMarginY.Value, 120f))
            {
                ChatBoxAnchor01.Value = new Vector2(0f, 0f);
                ChatBoxMarginY.Value = 60f;
                Logger.LogInfo("[ChatBox] one-shot migration: moved to bottom-left (above health bar)");
            }

            ShowInfo      = Config.Bind("ChatBox.Filters", "Show Info", true,      "Show general/informational notices (grey).");
            ShowPickup    = Config.Bind("ChatBox.Filters", "Show Pickup", true,    "Show item pickup notices (green).");
            ShowWarning   = Config.Bind("ChatBox.Filters", "Show Warning", true,   "Show warning-type notices like shield hints (yellow).");
            ShowBroadcast = Config.Bind("ChatBox.Filters", "Show Broadcast", true, "Show system broadcasts like horde warnings (orange).");

            SuppressGameNotifBubbles = Config.Bind("ChatBox.Suppress", "Suppress Game Notification Bubbles", true,
                "Prevent the game's default notification bubbles from appearing on-screen (we display them in the ChatBox instead).");
            SuppressGameHordeBanner = Config.Bind("ChatBox.Suppress", "Suppress Game Horde Banner", true,
                "Hide the game's top-right \"Survivor Broadcast: ...\" text banner (we mirror it into the ChatBox instead).");

            gameObject.AddComponent<ChatBox.ChatBox>();
            gameObject.AddComponent<HordeBroadcastPoller>();

            _harmony = new Harmony(PluginId);
            try
            {
                _harmony.PatchAll(typeof(Plugin).Assembly);
                Logger.LogInfo($"{PluginId} loaded — ChatBox online, notification interception + horde poller running");
            }
            catch (System.Exception e)
            {
                Logger.LogError($"{PluginId} Harmony patch failed: {e}");
            }

            // Seed a message per tag so first-run visually confirms the color coding + filters.
            HHMods.Core.Scheduler.Post(() =>
            {
                ChatBox.ChatBox.Post("HHMods QoL online — filters at the top let you show/hide tags.", ChatBox.NoticeTag.Info);
                ChatBox.ChatBox.Post("[ ChatBox ] x 1", ChatBox.NoticeTag.Pickup);
                ChatBox.ChatBox.Post("Warnings (like the shield hint) will show up here.", ChatBox.NoticeTag.Warning);
                ChatBox.ChatBox.Post("Survivor Broadcasts will appear here.", ChatBox.NoticeTag.Broadcast);
            });
        }
    }
}
