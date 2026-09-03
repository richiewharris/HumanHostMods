using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace HHMods.Perks
{
    /// <summary>
    /// One-shot diagnostic that walks the game's talent-skill arrays and dumps every perk
    /// (name, description, icon, per-rank values) to the BepInEx log AND to a text file
    /// next to the plugin. Uses reflection throughout since <c>TalentSkill</c> /
    /// <c>AllSkill</c> / <c>SkillValue</c> are private nested types on
    /// <c>All_Skills_Set</c> and <c>Skill_Mgr</c>.
    ///
    /// Fires once per session (idempotent) after Skill_Mgr and its SkillSets are populated.
    /// </summary>
    internal static class PerkDumper
    {
        private static bool _done;

        // Cached reflection handles — resolved lazily so a missing type in a future patch
        // fails loudly with a single log line, not a NRE storm.
        private static FieldInfo _fSkillSets;
        private static FieldInfo _fFightSkills, _fSurviveSkills, _fCraftSkills;
        private static FieldInfo _fTalentSkill, _fTalentClass;
        private static FieldInfo _fName, _fInstruct, _fIcon, _fMaxLv, _fIsBuff, _fBuffPeriod, _fStackable, _fValues;
        private static FieldInfo _fReplaceStrs;
        private static FieldInfo _fLangInfos, _fLangInfoText, _fLangInfoType;

        public static void DumpOnce()
        {
            if (_done) { return; }
            Plugin.Log.LogInfo("[PerkDump] entered");
            var sm = HHMods.Core.MgrHub.Skills;
            if (sm == null) { Plugin.Log.LogInfo("[PerkDump] Skill_Mgr not yet available; will retry on next apply"); return; }
            if (!ResolveReflection()) { Plugin.Log.LogWarning("[PerkDump] reflection resolve failed"); return; }
            var sets = _fSkillSets.GetValue(sm);
            if (sets == null) { Plugin.Log.LogWarning("[PerkDump] Skill_Mgr._SkillSets is null; will retry on next apply"); return; }
            _done = true;

            var sb = new StringBuilder();
            sb.AppendLine("======================================================================");
            sb.AppendLine($"HHMods.Perks — Game perk dump  ({System.DateTime.Now:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine("======================================================================");

            DumpCategory(sb, "COMBAT / FIGHT",   _fFightSkills.GetValue(sets)   as System.Array);
            DumpCategory(sb, "SURVIVAL",         _fSurviveSkills.GetValue(sets) as System.Array);
            DumpCategory(sb, "CRAFT / GATHER",   _fCraftSkills.GetValue(sets)   as System.Array);

            var body = sb.ToString();
            foreach (var line in body.Split('\n'))
                Plugin.Log.LogInfo(line.TrimEnd('\r'));

            // Also write a clean file for easy sharing.
            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var outPath = Path.Combine(pluginDir ?? ".", "perk-dump.txt");
                File.WriteAllText(outPath, body);
                Plugin.Log.LogInfo($"[PerkDump] wrote {outPath}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[PerkDump] file write failed: {e.Message}"); }
        }

        private static void DumpCategory(StringBuilder sb, string label, System.Array talents)
        {
            sb.AppendLine();
            sb.AppendLine($"---- {label} ----");
            if (talents == null) { sb.AppendLine("  (null array)"); return; }
            sb.AppendLine($"  count: {talents.Length}");

            for (int i = 0; i < talents.Length; i++)
            {
                var talent = talents.GetValue(i);
                if (talent == null) { sb.AppendLine($"  [{i:00}] (null talent)"); continue; }
                var skill = _fTalentSkill.GetValue(talent);
                if (skill == null) { sb.AppendLine($"  [{i:00}] (talent with null _skill)"); continue; }

                var name       = ResolveLanguageText(_fName.GetValue(skill))     ?? "(no name)";
                var instruct   = ResolveLanguageText(_fInstruct.GetValue(skill)) ?? "(no description)";
                var className  = ResolveLanguageText(_fTalentClass.GetValue(talent)) ?? "";
                var icon       = _fIcon.GetValue(skill) as Sprite;
                var iconName   = icon != null ? icon.name : "(no icon)";
                var iconTex    = icon != null && icon.texture != null ? $" tex={icon.texture.name}" : "";
                var maxLv      = (int)_fMaxLv.GetValue(skill);
                var isBuff     = (bool)_fIsBuff.GetValue(skill);
                var buffPeriod = (float)_fBuffPeriod.GetValue(skill);
                var stackable  = (bool)_fStackable.GetValue(skill);
                var valuesArr  = _fValues.GetValue(skill) as System.Array;

                sb.AppendLine();
                sb.AppendLine($"  [{i:00}] {name}   (class: {className})");
                sb.AppendLine($"       icon: {iconName}{iconTex}");
                sb.AppendLine($"       maxLv: {maxLv}   isBuff: {isBuff}   period: {buffPeriod}   stackable: {stackable}");
                sb.AppendLine($"       description: {instruct}");
                if (valuesArr != null && valuesArr.Length > 0)
                {
                    sb.AppendLine($"       per-rank replace values:");
                    for (int r = 0; r < valuesArr.Length; r++)
                    {
                        var v = valuesArr.GetValue(r);
                        if (v == null) { sb.AppendLine($"         rank {r + 1}: (null)"); continue; }
                        var strs = _fReplaceStrs.GetValue(v) as float[];
                        var joined = strs != null ? string.Join(", ", System.Array.ConvertAll(strs, f => f.ToString("0.###"))) : "(no _replaceStrs)";
                        sb.AppendLine($"         rank {r + 1}: [{joined}]");
                    }
                }
            }
        }

        /// <summary>
        /// Resolve a Language_Text to its display string. Prefers the game's own resolver
        /// (Language_Mgr.Get_Text) so it matches whatever locale the player uses; falls back
        /// to walking _Infos and taking the first entry if the resolver isn't available yet.
        /// </summary>
        private static string ResolveLanguageText(object lt)
        {
            if (lt == null) return null;
            try
            {
                var mgrType = System.Type.GetType("Language_Mgr, Language");
                if (mgrType != null)
                {
                    var insProp = mgrType.GetProperty("ins", BindingFlags.Public | BindingFlags.Static);
                    var mgr = insProp?.GetValue(null);
                    if (mgr != null)
                    {
                        var getText = mgrType.GetMethod("Get_Text", new[] { lt.GetType() });
                        if (getText != null) return (string)getText.Invoke(mgr, new[] { lt });
                    }
                }
            }
            catch { /* fall through */ }

            // Fallback: walk _Infos, prefer English (LanguageType.English if present), else first.
            try
            {
                if (_fLangInfos == null)
                    _fLangInfos = lt.GetType().GetField("_Infos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var infos = _fLangInfos?.GetValue(lt) as System.Collections.IList;
                if (infos == null || infos.Count == 0) return null;
                if (_fLangInfoText == null)
                {
                    var elemT = infos[0].GetType();
                    _fLangInfoText = elemT.GetField("text");
                    _fLangInfoType = elemT.GetField("languageType");
                }
                // Try English first
                foreach (var info in infos)
                {
                    var typ = _fLangInfoType?.GetValue(info)?.ToString();
                    if (typ != null && typ.IndexOf("English", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return _fLangInfoText?.GetValue(info) as string;
                }
                return _fLangInfoText?.GetValue(infos[0]) as string;
            }
            catch { return null; }
        }

        private static bool ResolveReflection()
        {
            if (_fValues != null) return true;   // already resolved
            try
            {
                var smType = System.Type.GetType("Skill_Mgr, Creature");
                if (smType == null) { Plugin.Log.LogError("[PerkDump] Skill_Mgr type not found"); return false; }

                _fSkillSets = smType.GetField("_SkillSets", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_fSkillSets == null) { Plugin.Log.LogError("[PerkDump] Skill_Mgr._SkillSets field not found"); return false; }

                var setType = _fSkillSets.FieldType;   // All_Skills_Set
                _fFightSkills   = setType.GetField("_FightSkills",   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fSurviveSkills = setType.GetField("_SurviveSkills", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fCraftSkills   = setType.GetField("_CraftSkills",   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                // TalentSkill nested on All_Skills_Set
                var talentType = setType.GetNestedType("TalentSkill", BindingFlags.Public | BindingFlags.NonPublic);
                _fTalentSkill = talentType?.GetField("_skill", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fTalentClass = talentType?.GetField("_class", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                // AllSkill nested on Skill_Mgr
                var allSkillType = smType.GetNestedType("AllSkill", BindingFlags.Public | BindingFlags.NonPublic);
                if (allSkillType == null) { Plugin.Log.LogError("[PerkDump] Skill_Mgr/AllSkill nested type not found"); return false; }
                _fName       = allSkillType.GetField("_name",       BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fInstruct   = allSkillType.GetField("_instruct",   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fIcon       = allSkillType.GetField("icon",        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fMaxLv      = allSkillType.GetField("maxLv",       BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fIsBuff     = allSkillType.GetField("isBuff",      BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fBuffPeriod = allSkillType.GetField("buffPeriod",  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fStackable  = allSkillType.GetField("stackableBuff", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fValues     = allSkillType.GetField("_values",     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                var svType = allSkillType.GetNestedType("SkillValue", BindingFlags.Public | BindingFlags.NonPublic);
                _fReplaceStrs = svType?.GetField("_replaceStrs", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                return _fValues != null && _fReplaceStrs != null && _fTalentSkill != null;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"[PerkDump] reflection init threw: {e}");
                return false;
            }
        }
    }
}
