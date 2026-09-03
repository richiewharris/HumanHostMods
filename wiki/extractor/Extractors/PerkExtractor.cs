// Extracts the game's talent tree: 3 categories (Fight / Survive / Craft), each holding a set of
// AllSkill entries with localized name, description, icon, max level, and per-level numeric values
// that get substituted into the description template.
//
// Source: the `All_Skills_Set` ScriptableObject in level1's assets, deserialized via AssetsTools.NET.
// The class layout comes from Creature.dll (`Skill_Mgr/AllSkill` nested type):
//   _name             Language_Text
//   _instruct         Language_Text
//   icon              Sprite (PPtr)
//   maxLv             int
//   isBuff            bool
//   buffPeriod        float
//   stackableBuff     bool
//   _values[]         SkillValue { _replaceStrs: float[] }   (one entry per level)
//
// Language_Text has `_Infos: List<Language_Info>` with 14 per-language entries. English is
// languageType 2 (same convention as the tooltip localization extractor).

using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace HHWiki.Extractor.Extractors;

public static class PerkExtractor
{
    public sealed record PerkLevel(int Level, float[] Values);
    public sealed record Perk(
        string Category,          // "Fight", "Survive", or "Craft"
        string InternalName,      // English text of the _name Language_Text
        string DisplayName,
        string Description,
        int MaxLevel,
        bool IsBuff,
        float BuffPeriodSeconds,
        bool StackableBuff,
        List<PerkLevel> Levels    // one entry per level, with per-level replacement numbers
    );

    private const int ENGLISH_LANGUAGE = 2;

    public static List<Perk>? Extract(string gameDir)
    {
        var dataDir = Path.Combine(gameDir, "Human Host_Data");
        var managedDir = Path.Combine(dataDir, "Managed");
        // All_Skills_Set is a ScriptableObject. It might live in level0, level1, resources.assets,
        // or one of the sharedassets*.assets files depending on where Unity chose to serialize it.
        // Try each in order until we find a MonoBehaviour carrying `_FightSkills`.
        var candidateFiles = new[]
        {
            Path.Combine(dataDir, "sharedassets0.assets"),
            Path.Combine(dataDir, "sharedassets1.assets"),
            Path.Combine(dataDir, "resources.assets"),
            Path.Combine(dataDir, "level0"),
            Path.Combine(dataDir, "level1"),
            Path.Combine(dataDir, "globalgamemanagers.assets"),
        }.Where(File.Exists).ToArray();

        var mgr = new AssetsManager();
        var tpkCandidates = new[]
        {
            Path.Combine(managedDir, "classdata.tpk"),
            Path.Combine(AppContext.BaseDirectory, "classdata.tpk"),
        };
        foreach (var p in tpkCandidates)
        {
            if (File.Exists(p)) { mgr.LoadClassPackage(p); break; }
        }
        mgr.MonoTempGenerator = new MonoCecilTempGenerator(managedDir);

        var perks = new List<Perk>();
        foreach (var af in candidateFiles)
        {
            var fileInst = mgr.LoadAssetsFile(af, true);
            if (mgr.ClassPackage != null)
            {
                mgr.LoadClassDatabaseFromPackage(fileInst.file.Metadata.UnityVersion);
            }
            if (TryExtractFrom(mgr, fileInst, af, perks)) break;
        }

        Console.WriteLine($"[perk] extracted {perks.Count} perks total");
        if (perks.Count == 0) { return null; }
        return perks;
    }

    private static bool TryExtractFrom(AssetsManager mgr, AssetsFileInstance fileInst, string sourceLabel, List<Perk> perks)
    {
        var mbList = fileInst.file.GetAssetsOfType(AssetClassID.MonoBehaviour).ToList();
        Console.WriteLine($"[perk] scanning {mbList.Count} MonoBehaviours in {Path.GetFileName(sourceLabel)}");
        int decodeOk = 0, sampleShown = 0;
        foreach (var info in mbList)
        {
            AssetTypeValueField root;
            try { root = mgr.GetBaseField(fileInst, info); }
            catch { continue; }
            if (root == null) continue;
            decodeOk++;
            if (sampleShown < 3 && root.Children.Count > 5) {
                Console.WriteLine($"[perk][sample MB] {string.Join(",", root.Children.Take(5).Select(c => c.FieldName))} ({root.Children.Count} fields)");
                sampleShown++;
            }

            var fight = root["_FightSkills"];
            if (fight == null || fight.IsDummy) continue;
            var survive = root["_SurviveSkills"];
            var craft = root["_CraftSkills"];
            Console.WriteLine("[perk] found All_Skills_Set");

            ExtractCategory(mgr, fileInst, fight, "Fight", perks);
            if (survive != null && !survive.IsDummy) ExtractCategory(mgr, fileInst, survive, "Survive", perks);
            if (craft != null && !craft.IsDummy) ExtractCategory(mgr, fileInst, craft, "Craft", perks);
            return true;
        }
        Console.WriteLine($"[perk] {Path.GetFileName(sourceLabel)}: decoded {decodeOk}/{mbList.Count} MonoBehaviours, no All_Skills_Set found");
        return false;
    }

    private static void ExtractCategory(AssetsManager mgr, AssetsFileInstance fileInst, AssetTypeValueField categoryField, string categoryName, List<Perk> outList)
    {
        var elements = ElementListOf(categoryField);
        if (elements == null) return;
        foreach (var talent in elements)
        {
            var skill = talent["_skill"];
            if (skill == null || skill.IsDummy) continue;
            var name = ReadLanguageTextPPtr(mgr, fileInst, skill["_name"], ENGLISH_LANGUAGE);
            var instr = ReadLanguageTextPPtr(mgr, fileInst, skill["_instruct"], ENGLISH_LANGUAGE);
            var maxLv = skill["maxLv"] != null && !skill["maxLv"].IsDummy ? skill["maxLv"].AsInt : 0;
            var isBuff = skill["isBuff"] != null && !skill["isBuff"].IsDummy && skill["isBuff"].AsBool;
            var buffPer = skill["buffPeriod"] != null && !skill["buffPeriod"].IsDummy ? skill["buffPeriod"].AsFloat : 0f;
            var stackBuff = skill["stackableBuff"] != null && !skill["stackableBuff"].IsDummy && skill["stackableBuff"].AsBool;

            var levels = new List<PerkLevel>();
            var valuesField = skill["_values"];
            var levelList = ElementListOf(valuesField);
            if (levelList != null)
            {
                int lv = 1;
                foreach (var levelEntry in levelList)
                {
                    var nums = new List<float>();
                    var replaceList = ElementListOf(levelEntry["_replaceStrs"]);
                    if (replaceList != null)
                    {
                        foreach (var n in replaceList) { nums.Add(n.AsFloat); }
                    }
                    levels.Add(new PerkLevel(lv++, nums.ToArray()));
                }
            }

            var display = string.IsNullOrEmpty(name) ? "(unnamed skill)" : name;
            outList.Add(new Perk(
                Category: categoryName,
                InternalName: name ?? "",
                DisplayName: display,
                Description: instr ?? "",
                MaxLevel: maxLv,
                IsBuff: isBuff,
                BuffPeriodSeconds: buffPer,
                StackableBuff: stackBuff,
                Levels: levels
            ));
        }
    }

    // The skill's _name / _instruct are PPtrs to Language_Text ScriptableObject assets. Resolve
    // the PPtr, read the resolved MonoBehaviour's _Infos list, and pull the English entry's text.
    private static string ReadLanguageTextPPtr(AssetsManager mgr, AssetsFileInstance fileInst, AssetTypeValueField? pptrField, int langType)
    {
        if (pptrField == null || pptrField.IsDummy) return "";
        AssetsTools.NET.Extra.AssetExternal ext;
        try { ext = mgr.GetExtAsset(fileInst, pptrField); }
        catch { return ""; }
        if (ext.info == null) return "";
        AssetTypeValueField root;
        try { root = mgr.GetBaseField(ext.file, ext.info); }
        catch { return ""; }
        if (root == null) return "";
        var infosField = root["_Infos"];
        if (infosField == null || infosField.IsDummy) return "";
        var infos = ElementListOf(infosField);
        if (infos == null) return "";
        foreach (var info in infos)
        {
            var lt = info["languageType"];
            if (lt == null || lt.IsDummy) continue;
            if (lt.AsInt != langType) continue;
            var text = info["text"];
            if (text != null && !text.IsDummy) return text.AsString ?? "";
            var itemName = info["_ItemName"];
            if (itemName != null && !itemName.IsDummy) return itemName.AsString ?? "";
        }
        return "";
    }

    // Same helper as TierExtractor: AssetsTools wraps arrays as field -> "Array" -> [size, data...].
    // Guards every access with an IsDummy check so a missing sub-field never throws.
    private static IReadOnlyList<AssetTypeValueField>? ElementListOf(AssetTypeValueField? f)
    {
        if (f == null || f.IsDummy) return null;
        if (f.Children.Count > 0 && f.Children[0].FieldName != "Array" && f.Children[0].FieldName != "size")
        {
            return f.Children;
        }
        AssetTypeValueField? arrayField = null;
        // Only try to descend into "Array" when the parent looks like it wraps one.
        if (f.Children.Any(c => c.FieldName == "Array"))
        {
            arrayField = f["Array"];
            if (arrayField != null && arrayField.IsDummy) arrayField = null;
        }
        arrayField ??= f;
        var elems = new List<AssetTypeValueField>(arrayField.Children.Count);
        foreach (var c in arrayField.Children)
        {
            if (c.FieldName == "size") continue;
            elems.Add(c);
        }
        return elems;
    }
}
