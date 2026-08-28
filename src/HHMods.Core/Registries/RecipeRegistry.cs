using System;
using System.Collections.Generic;
using UnityEngine.AddressableAssets;

namespace HHMods.Core
{
    public sealed class RecipeMaterial
    {
        public AssetReference IconRef { get; set; }
        public int Count { get; set; } = 1;
    }

    public sealed class RecipeDefinition
    {
        public string RecipeId { get; set; }
        public global::Craft_Mgr.WorkbenchType Workbench { get; set; } = global::Craft_Mgr.WorkbenchType.HandMade;
        public AssetReference OutputIconRef { get; set; }
        public int OutputCount { get; set; } = 1;
        public float CraftSeconds { get; set; } = 3f;
        public List<RecipeMaterial> Materials { get; set; } = new List<RecipeMaterial>();
    }

    /// <summary>
    /// Adds recipes to the game's data-driven crafting system by appending
    /// PerIconData entries to the appropriate CraftItemData.perIconData array
    /// under the target workbench.
    /// </summary>
    public static class RecipeRegistry
    {
        private static readonly List<RecipeDefinition> _pending = new List<RecipeDefinition>();
        private static bool _wired;

        public static void Register(RecipeDefinition recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            if (string.IsNullOrEmpty(recipe.RecipeId)) throw new ArgumentException("RecipeDefinition.RecipeId is required.");
            _pending.Add(recipe);
            EnsureWired();
        }

        private static void EnsureWired()
        {
            if (_wired) return;
            _wired = true;
            GameEvents.HubReady += ApplyAll;
        }

        private static void ApplyAll()
        {
            // TODO Wave 1/2: find Craft_Items instances for the target Workbench, resize their
            //     _CraftItemsData[i].perIconData arrays, append PerIconData built from our defs.
            //     Idempotent: skip if a matching RecipeId marker item is already present.
            foreach (var r in _pending)
            {
                Plugin.Log.LogInfo($"[RecipeRegistry] would install recipe {r.RecipeId} @ {r.Workbench}");
            }
        }
    }
}
