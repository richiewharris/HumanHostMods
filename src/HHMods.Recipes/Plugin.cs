using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HHMods.Core;
using UnityEngine;

namespace HHMods.Recipes
{
    /// <summary>
    /// Global recipe modifiers applied by rewriting the game's <c>Craft_Items._CraftItemsData</c>
    /// arrays after the crafting UI initializes. Three knobs:
    ///
    ///   1. Craft speed per workbench (multiplies each recipe's <c>craftSeconds</c>).
    ///   2. Material cost, global (multiplies each PerMatData's <c>matNeedCount</c>, floor 1).
    ///   3. Yield, global (multiplies each recipe's <c>craftNum</c>, floor 1).
    ///
    /// Originals are snapshotted before the first mutation so we can reset + reapply when the
    /// player edits config live. Snapshot is keyed by the PerIconData reference and survives
    /// while the crafting window / workbench exists; if the game replaces the data on a scene
    /// reload, the OnEnable postfix re-snapshots the fresh instances.
    ///
    /// The craft-speed knob is per-<c>WorkbenchType</c> because a single global speed value
    /// makes fast workbenches (Campfire) trivial and heavy ones (Furnace) still tedious. The
    /// material / yield knobs are global because those balance concerns aren't workbench-scoped.
    /// </summary>
    [BepInPlugin(PluginId, "HH Recipes", "0.1.0")]
    [BepInDependency(HHMods.Core.Plugin.PluginId)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.hh.recipes";

        internal static ManualLogSource Log { get; private set; }

        // ---- Global multipliers ----
        internal static ConfigEntry<float> MaterialCostMultiplier;
        internal static ConfigEntry<float> YieldMultiplier;

        // ---- Per-workbench craft speed multipliers (higher = faster crafting) ----
        internal static ConfigEntry<float> Speed_HandMade;
        internal static ConfigEntry<float> Speed_Campfire;
        internal static ConfigEntry<float> Speed_CarpentryWorkbench;
        internal static ConfigEntry<float> Speed_CuttingWorkbench;
        internal static ConfigEntry<float> Speed_AnvilWorkbench;
        internal static ConfigEntry<float> Speed_GunWorkbench;
        internal static ConfigEntry<float> Speed_MechanicalWorkbench;
        internal static ConfigEntry<float> Speed_BiochemicalWorkbench;
        internal static ConfigEntry<float> Speed_ElectronicsWorkbench;
        internal static ConfigEntry<float> Speed_Furnace;
        internal static ConfigEntry<float> Speed_CementMixer;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            MaterialCostMultiplier = Config.Bind("Global", "Material Cost Multiplier", 1.0f, R("Applied to every recipe's material count. 0.5 = half the materials. Floor 1 per material.", 0.05f, 10f));
            YieldMultiplier        = Config.Bind("Global", "Yield Multiplier",         1.0f, R("Applied to every recipe's output count. 2.0 = twice as many items per craft. Floor 1.", 0.1f, 20f));

            Speed_HandMade              = Config.Bind("Craft Speed (per workbench)", "HandMade",              1.0f, R("Craft speed multiplier for recipes made in the player's own hands (no workbench).", 0.1f, 20f));
            Speed_Campfire              = Config.Bind("Craft Speed (per workbench)", "Campfire",              1.0f, R("Craft speed multiplier at the Campfire.", 0.1f, 20f));
            Speed_CarpentryWorkbench    = Config.Bind("Craft Speed (per workbench)", "CarpentryWorkbench",    1.0f, R("Craft speed multiplier at the Carpentry Workbench.", 0.1f, 20f));
            Speed_CuttingWorkbench      = Config.Bind("Craft Speed (per workbench)", "CuttingWorkbench",      1.0f, R("Craft speed multiplier at the Cutting Workbench.", 0.1f, 20f));
            Speed_AnvilWorkbench        = Config.Bind("Craft Speed (per workbench)", "AnvilWorkbench",        1.0f, R("Craft speed multiplier at the Anvil / Smithing Workbench.", 0.1f, 20f));
            Speed_GunWorkbench          = Config.Bind("Craft Speed (per workbench)", "GunWorkbench",          1.0f, R("Craft speed multiplier at the Gun Workbench.", 0.1f, 20f));
            Speed_MechanicalWorkbench   = Config.Bind("Craft Speed (per workbench)", "MechanicalWorkbench",   1.0f, R("Craft speed multiplier at the Mechanical Workbench.", 0.1f, 20f));
            Speed_BiochemicalWorkbench  = Config.Bind("Craft Speed (per workbench)", "BiochemicalWorkbench",  1.0f, R("Craft speed multiplier at the Biochemical Workbench.", 0.1f, 20f));
            Speed_ElectronicsWorkbench  = Config.Bind("Craft Speed (per workbench)", "ElectronicsWorkbench",  1.0f, R("Craft speed multiplier at the Electronics Workbench.", 0.1f, 20f));
            Speed_Furnace               = Config.Bind("Craft Speed (per workbench)", "Furnace",               1.0f, R("Craft speed multiplier at the Furnace.", 0.1f, 20f));
            Speed_CementMixer           = Config.Bind("Craft Speed (per workbench)", "CementMixer",           1.0f, R("Craft speed multiplier at the Cement Mixer.", 0.1f, 20f));

            // ConfigFile-level event fires for any entry in this plugin's config file.
            Config.SettingChanged += (_, __) => Scheduler.Post(RecipeModifier.ReapplyAll);

            _harmony = new Harmony(PluginId);
            try
            {
                _harmony.PatchAll(typeof(Plugin).Assembly);
                Log.LogInfo($"{PluginId} loaded — Harmony patched (Craft_Items.OnEnable postfix)");
            }
            catch (System.Exception e) { Log.LogError($"[Recipes] Harmony patch failed: {e}"); }
        }

        private static ConfigDescription R(string desc, float min, float max) =>
            new ConfigDescription(desc, new AcceptableValueRange<float>(min, max));

        /// <summary>Looks up the craft-speed multiplier for a given workbench type.</summary>
        internal static float SpeedForWorkbench(global::Craft_Mgr.WorkbenchType type)
        {
            switch (type)
            {
                case global::Craft_Mgr.WorkbenchType.HandMade:              return Speed_HandMade.Value;
                case global::Craft_Mgr.WorkbenchType.Campfire:              return Speed_Campfire.Value;
                case global::Craft_Mgr.WorkbenchType.CarpentryWorkbench:    return Speed_CarpentryWorkbench.Value;
                case global::Craft_Mgr.WorkbenchType.CuttingWorkbench:      return Speed_CuttingWorkbench.Value;
                case global::Craft_Mgr.WorkbenchType.AnvilWorkbench:        return Speed_AnvilWorkbench.Value;
                case global::Craft_Mgr.WorkbenchType.GunWorkbench:          return Speed_GunWorkbench.Value;
                case global::Craft_Mgr.WorkbenchType.MechanicalWorkbench:   return Speed_MechanicalWorkbench.Value;
                case global::Craft_Mgr.WorkbenchType.BiochemicalWorkbench:  return Speed_BiochemicalWorkbench.Value;
                case global::Craft_Mgr.WorkbenchType.ElectronicsWorkbench:  return Speed_ElectronicsWorkbench.Value;
                case global::Craft_Mgr.WorkbenchType.Furnace:               return Speed_Furnace.Value;
                case global::Craft_Mgr.WorkbenchType.CementMixer:           return Speed_CementMixer.Value;
                default:                                                    return 1f;
            }
        }
    }

    /// <summary>
    /// Harmony patch: postfix on <c>Craft_Items.OnEnable</c>. Whenever a workbench UI opens
    /// (or the crafting HUD becomes visible), snapshot original recipe values (once per
    /// PerIconData instance) and apply current multipliers.
    /// </summary>
    [HarmonyPatch(typeof(global::Craft_Items), "OnEnable")]
    public static class Craft_Items_OnEnable_Patch
    {
        private static void Postfix(global::Craft_Items __instance) => RecipeModifier.ApplyTo(__instance);
    }

    /// <summary>
    /// Reflection-based mutator. <c>Craft_Items.CraftItemData</c>, <c>PerIconData</c>, and
    /// <c>PerMatData</c> are private nested types so we can't name them from an external
    /// assembly. AccessTools resolves them by string, and cached FieldInfo/PropertyInfo do
    /// the reads and writes. All access is done via <c>object</c> arrays and boxed values.
    /// </summary>
    internal static class RecipeModifier
    {
        private struct Original
        {
            public float CraftSeconds;
            public int CraftNum;
            public int[] MatCounts;
        }

        // Snapshot: PerIconData instance (as object) → original values.
        private static readonly System.Collections.Generic.Dictionary<object, Original> _originals
            = new System.Collections.Generic.Dictionary<object, Original>();

        // Track every Craft_Items we've patched so ReapplyAll can walk them on config change.
        private static readonly System.Collections.Generic.HashSet<global::Craft_Items> _patched
            = new System.Collections.Generic.HashSet<global::Craft_Items>();

        // Cached reflection handles for the nested types + private fields on Craft_Items.
        private static readonly System.Reflection.FieldInfo _fCraftItemsData =
            AccessTools.Field(typeof(global::Craft_Items), "_CraftItemsData");
        private static readonly System.Reflection.FieldInfo _fWorkbenchType =
            AccessTools.Field(typeof(global::Craft_Items), "_workbenchType");
        private static readonly System.Type _tCraftItemData =
            typeof(global::Craft_Items).GetNestedType("CraftItemData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        private static readonly System.Type _tPerIconData =
            typeof(global::Craft_Items).GetNestedType("PerIconData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        private static readonly System.Type _tPerMatData =
            typeof(global::Craft_Items).GetNestedType("PerMatData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        private static readonly System.Reflection.FieldInfo _fPerIconArr = _tCraftItemData?.GetField("perIconData");
        private static readonly System.Reflection.FieldInfo _fCraftSeconds = _tPerIconData?.GetField("craftSeconds");
        private static readonly System.Reflection.FieldInfo _fCraftNum = _tPerIconData?.GetField("craftNum");
        private static readonly System.Reflection.FieldInfo _fMatsData = _tPerIconData?.GetField("matsData");
        private static readonly System.Reflection.FieldInfo _fMatNeedCount = _tPerMatData?.GetField("matNeedCount");

        public static void ApplyTo(global::Craft_Items ci)
        {
            if (ci == null) return;
            if (_fCraftItemsData == null || _fPerIconArr == null || _fCraftSeconds == null
                || _fCraftNum == null || _fMatsData == null || _fMatNeedCount == null)
            {
                Plugin.Log?.LogError("[Recipes] reflection init failed — nested types not found. Was Craft_Items shape changed?");
                return;
            }
            var categoriesObj = _fCraftItemsData.GetValue(ci) as System.Array;
            if (categoriesObj == null) return;
            _patched.Add(ci);

            var speedMult = Plugin.SpeedForWorkbench((global::Craft_Mgr.WorkbenchType)_fWorkbenchType.GetValue(ci));
            var matMult = Mathf.Max(0.01f, Plugin.MaterialCostMultiplier.Value);
            var yieldMult = Mathf.Max(0.1f, Plugin.YieldMultiplier.Value);
            int touched = 0;

            foreach (var cat in categoriesObj)
            {
                if (cat == null) continue;
                var piArr = _fPerIconArr.GetValue(cat) as System.Array;
                if (piArr == null) continue;
                foreach (var pi in piArr)
                {
                    if (pi == null) continue;

                    if (!_originals.TryGetValue(pi, out var orig))
                    {
                        var matsArr = _fMatsData.GetValue(pi) as System.Array;
                        var matCounts = matsArr != null ? new int[matsArr.Length] : System.Array.Empty<int>();
                        if (matsArr != null)
                        {
                            for (int i = 0; i < matsArr.Length; i++)
                            {
                                var m = matsArr.GetValue(i);
                                matCounts[i] = m != null ? (int)_fMatNeedCount.GetValue(m) : 0;
                            }
                        }
                        orig = new Original
                        {
                            CraftSeconds = (float)_fCraftSeconds.GetValue(pi),
                            CraftNum = (int)_fCraftNum.GetValue(pi),
                            MatCounts = matCounts,
                        };
                        _originals[pi] = orig;
                    }

                    _fCraftSeconds.SetValue(pi, orig.CraftSeconds / speedMult);
                    _fCraftNum.SetValue(pi, Mathf.Max(1, Mathf.RoundToInt(orig.CraftNum * yieldMult)));

                    var writeMats = _fMatsData.GetValue(pi) as System.Array;
                    if (writeMats != null)
                    {
                        for (int i = 0; i < writeMats.Length && i < orig.MatCounts.Length; i++)
                        {
                            var m = writeMats.GetValue(i);
                            if (m == null) continue;
                            _fMatNeedCount.SetValue(m, Mathf.Max(1, Mathf.RoundToInt(orig.MatCounts[i] * matMult)));
                        }
                    }
                    touched++;
                }
            }
            Plugin.Log?.LogInfo($"[Recipes] applied to {(global::Craft_Mgr.WorkbenchType)_fWorkbenchType.GetValue(ci)}: {touched} recipes  speed×{speedMult}  mats×{matMult}  yield×{yieldMult}");
        }

        /// <summary>Reapplies to every Craft_Items we've seen. Called on config change.</summary>
        public static void ReapplyAll()
        {
            _patched.RemoveWhere(ci => ci == null);
            foreach (var ci in _patched) ApplyTo(ci);
        }
    }
}
