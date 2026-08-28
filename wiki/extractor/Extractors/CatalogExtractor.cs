using System.Text;
using System.Text.Json;

namespace HHWiki.Extractor.Extractors;

/// <summary>
/// Reads Unity Addressables' StreamingAssets/aa/catalog.json and pulls the readable asset keys
/// (m_KeyDataString, base64-encoded). This is dramatically cleaner than scanning bundle bytes:
/// the catalog holds every addressable asset's canonical name.
///
/// Buckets keys by prefix so wiki pages can map straight from prefix -> category:
///   BO_    -> Build Ore (mineable ore blocks + non-ore rocks)
///   BOI_   -> Build Ore Icon
///   BT_    -> Build Trap
///   BTI_   -> Build Trap Icon
///   BF_    -> Build Function (workbenches, storage)
///   BFI_   -> Build Function Icon
///   BW_    -> Build Wall
///   BWI_   -> Build Wall Icon
///   BB_ / N_N_ -> Build Base (foundations with material-tier numeric prefix)
///   FWI_   -> Food / Water Icon
///   RII_   -> Refined / Resource Item Icon
///   RWI_   -> Ranged Weapon Icon (arrows, bolts)
///   RW_    -> Ranged Weapon
///   BHC_   -> Bullet Hollow-Cased ammunition (calibers)
///   AXE_ / DAG_ / SPEAR_ / BIG_ / TOOL_ -> Melee weapons + tools
///
/// The numeric material-tier prefixes on building assets follow a discovered pattern:
///   1_x = wood tier (Damaged Planks, Plank_Old, Plank_Poplar, Plank_Red_Oak, Plank_Ebony)
///   2_x = brick tier (Damaged Brick, Cellular Brick, Lime Brick, Silicon Sulfide Brick, Carbon-Bonded Brick)
///   3_x = cement/concrete tier (Cement, Concrete, Ferrock Concrete, Reinforced Concrete, Titanium Mesh Concrete)
///   4_x = iron tier (Rust Iron, Pig Iron, Wrought Iron, Cast Iron, Chrome Iron)
///   5_x = alloy tier (Copper Alloy, Titanium Alloy, High-Speed Steel, Chrome Alloy, Tungsten Steel)
///   6_x = glass tier
///   7_x = decorative tier (Marble, Epidote, Lapis Lazuli, Obsidian)
/// </summary>
public static class CatalogExtractor
{
    public sealed record Catalog(
        int TotalKeys,
        List<string> Ores,
        List<string> Foods,
        List<string> Waters,
        List<string> RefinedResources,
        List<string> Ammunition,
        List<string> MeleeWeapons,
        List<string> RangedWeapons,
        List<string> Tools,
        List<string> Traps,
        List<BuildTier> BuildTiers,
        List<string> Functions);

    public sealed record BuildTier(string TierName, string TierPrefix, List<string> Materials);

    public static Catalog Extract(DirectoryInfo gameDir)
    {
        var catalogPath = Path.Combine(gameDir.FullName, "Human Host_Data", "StreamingAssets", "aa", "catalog.json");
        if (!File.Exists(catalogPath)) throw new FileNotFoundException($"Addressables catalog not found: {catalogPath}");

        // The catalog is a giant JSON object; we only care about m_KeyDataString.
        using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var keyDataB64 = doc.RootElement.GetProperty("m_KeyDataString").GetString()
            ?? throw new InvalidDataException("m_KeyDataString missing");
        var bytes = Convert.FromBase64String(keyDataB64);

        // Extract every printable-ASCII run of >=4 chars. Addressables serializes each string with a
        // 4-byte type header then the string, so runs cleanly demarcate individual keys.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b >= 32 && b < 127) { sb.Append((char)b); }
            else { if (sb.Length >= 4) keys.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= 4) keys.Add(sb.ToString());

        // Bucket by prefix
        var ores = FilterPrefix(keys, "BO_", excludeSuffix: "_Icon");
        var foods = new List<string>();
        var waters = new List<string>();

        // Food/water combined under FWI_; split on obvious water items
        foreach (var k in keys.Where(k => k.StartsWith("FWI_") && k.EndsWith("_Icon")))
        {
            var name = k.Substring("FWI_".Length, k.Length - "FWI_".Length - "_Icon".Length);
            if (name.Contains("Water") || name.Contains("Drink") || name.Contains("Juice") || name.Contains("Soda"))
                waters.Add(name);
            else
                foods.Add(name);
        }

        var refined  = FilterPrefix(keys, "RII_", excludeSuffix: "_Icon");
        var ammoAll  = FilterPrefix(keys, "BHC_", excludeSuffix: "_Icon");
        // Also include bullet-tip components
        var ammoTips = keys.Where(k => k.StartsWith("Bullet_Tip_") && k.EndsWith("_Icon"))
                           .Select(k => k.Substring(0, k.Length - "_Icon".Length))
                           .OrderBy(s => s);
        var ammunition = ammoAll.Concat(ammoTips).Distinct(StringComparer.Ordinal).OrderBy(s => s).ToList();

        var melee = new List<string>();
        foreach (var prefix in new[] { "AXE_", "DAG_", "SPEAR_", "BIG_" })
        {
            melee.AddRange(FilterPrefix(keys, prefix, excludeSuffix: "_Icon"));
        }
        melee = melee.Distinct(StringComparer.Ordinal).OrderBy(s => s).ToList();

        var ranged = FilterPrefix(keys, "RW_", excludeSuffix: "_Icon");
        var tools  = FilterPrefix(keys, "TOOL_", excludeSuffix: "_Icon");
        var traps  = FilterPrefix(keys, "BT_", excludeSuffix: "_Icon");
        var functions = FilterPrefix(keys, "BF_", excludeSuffix: "_Icon");

        // Build tiers: keys matching ^N_M_<shape>_<material>$ where the material portion is what we want per tier
        var buildTiers = ExtractBuildTiers(keys);

        return new Catalog(
            TotalKeys: keys.Count,
            Ores: ores,
            Foods: foods.OrderBy(s => s).ToList(),
            Waters: waters.OrderBy(s => s).ToList(),
            RefinedResources: refined,
            Ammunition: ammunition,
            MeleeWeapons: melee,
            RangedWeapons: ranged,
            Tools: tools,
            Traps: traps,
            BuildTiers: buildTiers,
            Functions: functions);
    }

    private static List<string> FilterPrefix(HashSet<string> keys, string prefix, string? excludeSuffix)
    {
        return keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Where(k => excludeSuffix == null || !k.EndsWith(excludeSuffix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    private static readonly Dictionary<string, string> TierNames = new()
    {
        ["1"] = "Wood",
        ["2"] = "Brick",
        ["3"] = "Cement / Concrete",
        ["4"] = "Iron",
        ["5"] = "Alloy / Steel",
        ["6"] = "Glass",
        ["7"] = "Decorative Stone",
    };

    private static List<BuildTier> ExtractBuildTiers(HashSet<string> keys)
    {
        // Keys look like: "4_1_Block_1.4_Rust_Iron", "5_3_Cuboid_High-Speed_Steel"
        // We want the material portion (Rust_Iron, High-Speed_Steel) grouped by tier (4_1, 5_3, ...)
        // Then aggregate by top-level tier group (4_x = Iron).
        var tierMaterials = new Dictionary<string, HashSet<string>>();  // "4" -> {Rust_Iron, Pig_Iron, ...}

        var shapeWords = new HashSet<string>(StringComparer.Ordinal) {
            "Block", "Cuboid", "Cylinder", "Half", "Pyramid", "Squat", "Tall", "Steps",
            "Curved", "Triangle", "Corner", "CornerS", "Small", "L", "M", "S"
        };

        foreach (var k in keys)
        {
            if (k.EndsWith("_Icon")) continue;
            var parts = k.Split('_');
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[0], out var tier1)) continue;
            if (!int.TryParse(parts[1], out _)) continue;
            if (tier1 < 1 || tier1 > 7) continue;

            // Material name = everything after the shape words. Walk from the end backwards; take
            // trailing tokens until we hit a shape word or a number.
            var material = new List<string>();
            for (int i = parts.Length - 1; i >= 2; i--)
            {
                var p = parts[i];
                if (shapeWords.Contains(p)) break;
                if (decimal.TryParse(p, out _)) break;
                material.Insert(0, p);
            }
            if (material.Count == 0) continue;
            var matName = string.Join(" ", material);
            var tierKey = parts[0];
            if (!tierMaterials.TryGetValue(tierKey, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                tierMaterials[tierKey] = set;
            }
            set.Add(matName);
        }

        var results = new List<BuildTier>();
        foreach (var kvp in tierMaterials.OrderBy(k => k.Key))
        {
            var name = TierNames.TryGetValue(kvp.Key, out var n) ? n : $"Tier {kvp.Key}";
            results.Add(new BuildTier(name, kvp.Key, kvp.Value.OrderBy(s => s).ToList()));
        }
        return results;
    }
}
