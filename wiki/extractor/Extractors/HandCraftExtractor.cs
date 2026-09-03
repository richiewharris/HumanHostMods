// Extracts the HandMade "craft by hand" recipe list. This is a scene-embedded Craft_Items
// MonoBehaviour in level1.unity whose _workbenchType field is set to HandMade. AssetRipper
// dumps this MonoBehaviour empty because it can't resolve the Craft_Items class layout, so we
// use AssetsTools.NET (same pipeline as TierExtractor and PerkExtractor).
//
// Output: data/handmade_recipes.json with each recipe's output GUID, craft time, and inputs.
// The wiki generator resolves GUIDs to display names via the existing guid_name_map.

using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace HHWiki.Extractor.Extractors;

public static class HandCraftExtractor
{
    public sealed record MatData(string Guid, int NeedCount);
    public sealed record Recipe(string OutputGuid, int CraftNum, float CraftSeconds, List<MatData> Inputs);
    public sealed record CategoryBlock(string CategoryLabel, List<Recipe> Recipes);
    public sealed record HandCraftData(int WorkbenchTypeId, List<CategoryBlock> Categories);

    public static HandCraftData? Extract(string gameDir)
    {
        var dataDir = Path.Combine(gameDir, "Human Host_Data");
        var managedDir = Path.Combine(dataDir, "Managed");
        var levelFile = Path.Combine(dataDir, "level1");
        if (!File.Exists(levelFile)) { Console.WriteLine($"[hand] missing {levelFile}"); return null; }

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
        var fileInst = mgr.LoadAssetsFile(levelFile, true);
        if (mgr.ClassPackage != null)
        {
            mgr.LoadClassDatabaseFromPackage(fileInst.file.Metadata.UnityVersion);
        }

        var mbList = fileInst.file.GetAssetsOfType(AssetClassID.MonoBehaviour).ToList();
        Console.WriteLine($"[hand] scanning {mbList.Count} MonoBehaviours in {Path.GetFileName(levelFile)}");
        foreach (var info in mbList)
        {
            AssetTypeValueField root;
            try { root = mgr.GetBaseField(fileInst, info); }
            catch { continue; }
            if (root == null) continue;

            var wbType = root["_workbenchType"];
            if (wbType == null || wbType.IsDummy) continue;
            var craftItemsData = root["_CraftItemsData"];
            if (craftItemsData == null || craftItemsData.IsDummy) continue;

            var wbTypeId = wbType.AsInt;
            // HandMade shows up as workbenchType id NOT observed in recipes.json's workbench_enum_observed
            // (that map covered 1-10 for the 10 workbench prefabs). This one should be a different value.
            Console.WriteLine($"[hand] found Craft_Items MonoBehaviour with _workbenchType={wbTypeId}");

            var categories = ExtractCategories(craftItemsData);
            return new HandCraftData(WorkbenchTypeId: wbTypeId, Categories: categories);
        }
        Console.WriteLine("[hand] no scene-embedded Craft_Items MonoBehaviour found");
        return null;
    }

    private static List<CategoryBlock> ExtractCategories(AssetTypeValueField craftItemsDataField)
    {
        var cats = new List<CategoryBlock>();
        var catElems = ElementListOf(craftItemsDataField);
        if (catElems == null) return cats;
        int catIdx = 0;
        foreach (var cat in catElems)
        {
            var perIcon = cat["perIconData"];
            if (perIcon == null || perIcon.IsDummy) continue;
            var recipes = new List<Recipe>();
            var iconElems = ElementListOf(perIcon);
            if (iconElems == null) continue;
            foreach (var rec in iconElems)
            {
                var iconRef = rec["iconRef"];
                var outputGuid = iconRef != null && !iconRef.IsDummy ? ReadAssetGuid(iconRef) : "";
                var craftNum = rec["craftNum"] != null && !rec["craftNum"].IsDummy ? rec["craftNum"].AsInt : 0;
                var craftSec = rec["craftSeconds"] != null && !rec["craftSeconds"].IsDummy ? rec["craftSeconds"].AsFloat : 0f;
                var matsData = rec["matsData"];
                var inputs = new List<MatData>();
                var matElems = ElementListOf(matsData);
                if (matElems != null)
                {
                    foreach (var mat in matElems)
                    {
                        var matIcon = mat["matIcon"];
                        var matGuid = matIcon != null && !matIcon.IsDummy ? ReadAssetGuid(matIcon) : "";
                        var matNeed = mat["matNeedCount"] != null && !mat["matNeedCount"].IsDummy ? mat["matNeedCount"].AsInt : 0;
                        inputs.Add(new MatData(matGuid, matNeed));
                    }
                }
                recipes.Add(new Recipe(outputGuid, craftNum, craftSec, inputs));
            }
            cats.Add(new CategoryBlock($"Category_{catIdx++}", recipes));
        }
        return cats;
    }

    private static string ReadAssetGuid(AssetTypeValueField assetRefField)
    {
        // AssetReference wraps m_AssetGUID as its `_assetGUID` child (private field, but serialized).
        // Try common field names in order.
        foreach (var candidate in new[] { "m_AssetGUID", "_assetGUID", "assetGUID", "guid" })
        {
            var f = assetRefField[candidate];
            if (f != null && !f.IsDummy) { var s = f.AsString; if (!string.IsNullOrEmpty(s)) return s; }
        }
        // Fallback: iterate children looking for a 32-char hex string
        foreach (var c in assetRefField.Children)
        {
            try { var s = c.AsString; if (!string.IsNullOrEmpty(s) && s.Length == 32) return s; } catch { }
        }
        return "";
    }

    private static IReadOnlyList<AssetTypeValueField>? ElementListOf(AssetTypeValueField? f)
    {
        if (f == null || f.IsDummy) return null;
        if (f.Children.Count > 0 && f.Children[0].FieldName != "Array" && f.Children[0].FieldName != "size")
        {
            return f.Children;
        }
        AssetTypeValueField? arrayField = null;
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
