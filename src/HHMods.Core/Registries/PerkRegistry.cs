using System;
using System.Collections.Generic;
using UnityEngine;

namespace HHMods.Core
{
    /// <summary>
    /// Which of the game's three built-in talent trees to add a perk to.
    /// </summary>
    public enum PerkTree
    {
        Fight,
        Survive,
        Craft,
    }

    /// <summary>
    /// Definition of a perk that <see cref="PerkRegistry"/> will register into the game's
    /// existing <c>Skill_Mgr</c> talent system.
    /// </summary>
    public sealed class PerkDefinition
    {
        /// <summary>Unique English name used as the lookup key (matches Skill_Data.Get_Learned_Skill_Lv).</summary>
        public string NameEn { get; set; }
        /// <summary>Human-readable display name (defaults to NameEn).</summary>
        public string DisplayName { get; set; }
        /// <summary>Tooltip / instruction text.</summary>
        public string Description { get; set; }
        /// <summary>Which tree.</summary>
        public PerkTree Tree { get; set; } = PerkTree.Survive;
        /// <summary>Max rank (1 = single-level perk).</summary>
        public int MaxLevel { get; set; } = 5;
        /// <summary>Icon sprite. If null, a placeholder is used.</summary>
        public Sprite Icon { get; set; }
        /// <summary>Value per rank — arbitrary shape, consumed by the owning plugin.</summary>
        public float[] ValuePerRank { get; set; }
        /// <summary>Called when the player's rank in this perk changes. Rank 0 = unlearned.</summary>
        public Action<int> OnRankChanged { get; set; }
    }

    /// <summary>
    /// Registers perks into the game's <c>All_Skills_Set</c> ScriptableObject at runtime.
    /// Actual registration is deferred until <see cref="GameEvents.HubReady"/> fires.
    /// </summary>
    public static class PerkRegistry
    {
        private static readonly List<PerkDefinition> _pending = new List<PerkDefinition>();
        private static readonly Dictionary<string, PerkDefinition> _byName = new Dictionary<string, PerkDefinition>();
        private static bool _wired;

        /// <summary>Register a perk. Safe to call at plugin Awake — deferred until game is ready.</summary>
        public static void Register(PerkDefinition perk)
        {
            if (perk == null) throw new ArgumentNullException(nameof(perk));
            if (string.IsNullOrEmpty(perk.NameEn)) throw new ArgumentException("PerkDefinition.NameEn is required.");
            if (_byName.ContainsKey(perk.NameEn))
            {
                Plugin.Log.LogWarning($"[PerkRegistry] {perk.NameEn} already registered — replacing.");
            }
            _byName[perk.NameEn] = perk;
            _pending.Add(perk);
            EnsureWired();
        }

        /// <summary>All registered perks, keyed by NameEn.</summary>
        public static IReadOnlyDictionary<string, PerkDefinition> AllByName => _byName;

        /// <summary>Current rank of the given perk for the local player, or 0.</summary>
        public static int GetRank(string perkNameEn)
        {
            var skills = MgrHub.LocalCharSkills;
            var data = skills?._Data;
            if (data == null) return 0;
            return data.Get_Learned_Skill_Lv(perkNameEn);
        }

        private static void EnsureWired()
        {
            if (_wired) return;
            _wired = true;
            GameEvents.HubReady += ApplyAll;
        }

        private static void ApplyAll()
        {
            var skillMgr = MgrHub.Skills;
            if (skillMgr == null)
            {
                Plugin.Log.LogWarning("[PerkRegistry] Skill_Mgr unavailable at HubReady — will retry.");
                return;
            }

            // TODO Wave 2: reflect into All_Skills_Set._SurviveSkills / _CraftSkills / _FightSkills,
            //              build an AllSkill from the definition, wrap in TalentSkill, and append.
            //              For now this just logs so the pipeline can be validated end-to-end.
            foreach (var p in _pending)
            {
                Plugin.Log.LogInfo($"[PerkRegistry] would install {p.Tree}/{p.NameEn} (maxLv={p.MaxLevel})");
            }
        }
    }
}
