using UnityEngine;

namespace HHMods.Core
{
    /// <summary>
    /// Safe, null-guarded accessors for every manager exposed by the game's central
    /// <c>Mgr_Hub</c> singleton. Every getter returns null when the game isn't ready.
    /// </summary>
    public static class MgrHub
    {
        /// <summary>The hub itself, or null if not yet spawned.</summary>
        public static global::Mgr_Hub HubOrNull
        {
            get
            {
                // Mgr_Hub is a MonoBehaviour in the scene, not a singleton with _ins.
                // Cheapest resolve is Object.FindObjectOfType — cache once found.
                if (_cachedHub != null) return _cachedHub;
                _cachedHub = Object.FindObjectOfType<global::Mgr_Hub>();
                return _cachedHub;
            }
        }
        private static global::Mgr_Hub _cachedHub;

        internal static void ForgetHub() => _cachedHub = null;

        // ---- Common accessors ----
        public static global::Skill_Mgr Skills => HubOrNull?._SkillMgr;
        public static global::Player_Mgr Players => HubOrNull?._PlayerMgr;
        public static global::Craft_Mgr Craft => HubOrNull?._CraftMgr;
        public static global::Loot_Mgr Loot => HubOrNull?._LootMgr;
        public static global::Terrain_Dig TerrainDig => HubOrNull?._TerraDigMgr;
        public static global::Trap_Mgr Traps => HubOrNull?._TrapMgr;
        public static global::World_Map_Mgr WorldMap => HubOrNull?._WorldMapMgr;
        public static global::NotificationSystem Notifications => HubOrNull?._NotificationSystem;
        public static global::Player_HotKeys Hotkeys => HubOrNull?._Player_HotKeys;
        public static global::SaveDataManager Save => HubOrNull?._SaveDataMgr;
        public static global::Car_Mgr Cars => HubOrNull?._CarMgr;
        public static global::Equipment_Mgr Equipment => HubOrNull?._EquipmentMgr;
        public static global::CamController Cam => HubOrNull?._CamController;
        public static global::Global_Infos GlobalInfos => HubOrNull?._GlobalInfos;

        /// <summary>The active local player's Char_Skills component, or null.</summary>
        public static global::Char_Skills LocalCharSkills
        {
            get
            {
                // Player_Mgr doesn't directly expose the character; search for a Char_Skills tagged as player.
                // Cheap enough since scene has one player.
                return Object.FindObjectOfType<global::Char_Skills>();
            }
        }
    }
}
