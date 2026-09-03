using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HHMods.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HHMods.Perks
{
    /// <summary>
    /// Passive perks applied by writing modifier fields on the local player's
    /// <c>Char_Skills</c> component. All perks are "always on" (no XP gating); each is a
    /// float multiplier or additive bonus configured via BepInEx. Defaults are all neutral
    /// (1.0 for multipliers, 0.0 for additive boosts), so the mod ships transparent.
    ///
    /// Applies on <c>GameEvents.HubReady</c>, on scene load, and whenever a config value
    /// changes. Uses a short retry loop after HubReady so Char_Skills gets picked up whether
    /// it's spawned with the player or a few frames later.
    /// </summary>
    [BepInPlugin(PluginId, "HH Perks", "0.1.0")]
    [BepInDependency(HHMods.Core.Plugin.PluginId)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.hh.perks";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        // ---- Mining / gathering ----
        internal static ConfigEntry<float> MinerDmgFactor;
        internal static ConfigEntry<float> WoodJackDmgFactor;
        internal static ConfigEntry<float> WoodJackYieldFactor;
        internal static ConfigEntry<float> LootCountBoost;
        internal static ConfigEntry<float> LootQualityBoost;
        internal static ConfigEntry<float> LootOpenSpeedRate;

        // ---- Tool durability ----
        internal static ConfigEntry<float> DuraCostPerAttackMod;   // < 1 = tools last longer
        internal static ConfigEntry<float> RepairFactor;

        // ---- Vitals ----
        internal static ConfigEntry<float> MaxHPFactor;
        internal static ConfigEntry<float> MaxStaminaFactor;
        internal static ConfigEntry<float> StaminaRegeFactor;

        // ---- Combat (opt-in; leave 1.0 for vanilla balance) ----
        internal static ConfigEntry<float> MeleeDamageFactor;
        internal static ConfigEntry<float> GunDamageFactor;
        internal static ConfigEntry<float> BowDamageFactor;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            MinerDmgFactor       = Config.Bind("Gathering", "Miner Damage Factor",       1.0f, R("Multiplier applied to pickaxe / mining damage. 2.0 = twice as fast to mine.", 0.1f, 10f));
            WoodJackDmgFactor    = Config.Bind("Gathering", "WoodJack Damage Factor",    1.0f, R("Multiplier applied to axe / chopping damage on trees.", 0.1f, 10f));
            WoodJackYieldFactor  = Config.Bind("Gathering", "WoodJack Yield Factor",     1.0f, R("Multiplier applied to wood harvested per swing. 2.0 = twice the yield per hit.", 0.1f, 10f));
            LootCountBoost       = Config.Bind("Gathering", "Loot Count Boost",          0.0f, R("Additive bonus to the extra-loot roll rate. 0 = vanilla, 0.5 = +50% chance of extra items.", 0f, 5f));
            LootQualityBoost     = Config.Bind("Gathering", "Loot Quality Boost",        0.0f, R("Additive bonus to the loot-quality roll rate. Higher = more chance of rare drops.", 0f, 5f));
            LootOpenSpeedRate    = Config.Bind("Gathering", "Loot Open Speed Rate",      1.0f, R("Multiplier on the delay to open loot crates. 2.0 = twice as fast (delay halved).", 0.1f, 10f));

            DuraCostPerAttackMod = Config.Bind("Durability", "Durability Cost Per Attack Mod", 1.0f, R("Multiplier on the durability lost per swing. 0.5 = tools last twice as long. 0 = tools never wear.", 0f, 5f));
            RepairFactor         = Config.Bind("Durability", "Repair Factor",                  1.0f, R("Multiplier on durability restored per repair action.", 0.1f, 10f));

            MaxHPFactor          = Config.Bind("Vitals",     "Max HP Factor",                  1.0f, R("Multiplier on the character's max HP.", 0.1f, 10f));
            MaxStaminaFactor     = Config.Bind("Vitals",     "Max Stamina Factor",             1.0f, R("Multiplier on the character's max stamina.", 0.1f, 10f));
            StaminaRegeFactor    = Config.Bind("Vitals",     "Stamina Regen Factor",           1.0f, R("Multiplier on stamina regeneration rate.", 0.1f, 10f));

            MeleeDamageFactor    = Config.Bind("Combat",     "Melee Damage Factor",            1.0f, R("Multiplier on melee weapon damage.", 0.1f, 10f));
            GunDamageFactor      = Config.Bind("Combat",     "Gun Damage Factor",              1.0f, R("Multiplier on firearm damage.", 0.1f, 10f));
            BowDamageFactor      = Config.Bind("Combat",     "Bow Damage Factor",              1.0f, R("Multiplier on bow damage.", 0.1f, 10f));

            // Re-apply whenever any config changes so tweaks take effect live without a scene reload.
            // ConfigFile-level event fires for any entry in this plugin's config file.
            Config.SettingChanged += (_, __) => Scheduler.Post(() => ApplyToLocalPlayer());

            GameEvents.HubReady += OnHubReady;
            SceneManager.sceneLoaded += (_, __) => StartCoroutine(DelayedApply(0.5f));
            Log.LogInfo($"{PluginId} loaded — awaiting HubReady");
        }

        private System.Collections.IEnumerator DelayedApply(float sec)
        {
            yield return new WaitForSeconds(sec);
            ApplyToLocalPlayer();
        }

        private static ConfigDescription R(string desc, float min, float max) =>
            new ConfigDescription(desc, new AcceptableValueRange<float>(min, max));

        private void OnHubReady()
        {
            // Char_Skills may spawn a few frames after Hub. Retry on a short cadence until we
            // land it or give up.
            StartCoroutine(RetryApply());
        }

        private System.Collections.IEnumerator RetryApply()
        {
            for (int i = 0; i < 20; i++)
            {
                if (ApplyToLocalPlayer())
                {
                    // One-shot diagnostic dump of the game's real perk tree so we can align our
                    // mod perks against actual names / descriptions / icons. Logs + writes a file.
                    PerkDumper.DumpOnce();
                    yield break;
                }
                yield return new WaitForSeconds(0.5f);
            }
            Log.LogWarning("[Perks] Char_Skills not found after 10s — perks not applied. Will retry on next scene load / config change.");
        }

        /// <summary>Writes every configured perk value onto the local player's Char_Skills.</summary>
        internal static bool ApplyToLocalPlayer()
        {
            var cs = MgrHub.LocalCharSkills;
            if (cs == null) return false;

            cs._minerDmg_Factor           = MinerDmgFactor.Value;
            cs._woodJackDmg_Factor        = WoodJackDmgFactor.Value;
            cs._woodJackYield_Factor      = WoodJackYieldFactor.Value;
            cs._lootCountRateBoost        = LootCountBoost.Value;
            cs._lootQualityRateBoost      = LootQualityBoost.Value;
            cs._lootOpenSpeedRate         = LootOpenSpeedRate.Value;

            cs._duraCostPerAttack_Mod     = DuraCostPerAttackMod.Value;
            cs._repair_Factor             = RepairFactor.Value;

            cs._maxHP_Factor              = MaxHPFactor.Value;
            cs._maxStamina_Factor         = MaxStaminaFactor.Value;
            cs._staminaRege_Factor        = StaminaRegeFactor.Value;

            cs._meleeDamage_Factor        = MeleeDamageFactor.Value;
            cs._gunDamage_Factor          = GunDamageFactor.Value;
            cs._bowDamage_Factor          = BowDamageFactor.Value;

            Log?.LogInfo($"[Perks] applied → miner×{cs._minerDmg_Factor} wood×{cs._woodJackDmg_Factor}/y{cs._woodJackYield_Factor} dura×{cs._duraCostPerAttack_Mod} hp×{cs._maxHP_Factor} stam×{cs._maxStamina_Factor}");
            // Dumper is idempotent (guarded by its own _done flag); safe to call every apply
            // so we catch the first successful moment regardless of which path got us here.
            PerkDumper.DumpOnce();
            return true;
        }
    }
}
