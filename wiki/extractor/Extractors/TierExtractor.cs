// Extracts the game's quality/tier system: per-tier damage/dura/hitdown/blade multipliers
// and the color codes used for each tier's tooltip border. The data lives on a scene-embedded
// Item_Slot_Mgr MonoBehaviour in level1; the class definition ships in UI.dll.
//
// Fields we pull (all defined on Item_Slot_Mgr in UI.dll):
//   _qualityDamageFactors   : float[]  per-tier damage multiplier
//   _qualityDuraFactors     : float[]  per-tier durability multiplier
//   _qualityHitDownFactors  : float[]  per-tier hit-down probability multiplier
//   _qualityBladeHitFactors : float[]  per-tier blade slash probability multiplier
//   _qualityRandomRates     : List<float>  loot spawn weights per tier
//   _QualityTooltipColors   : Color[] (RGBA 0-1)  border tint per tier
//   _slotQualitySprites     : Sprite[]  quality corner icon
//   _MaxLootLevel           : int   scaling reference
//   _MaxCraftLevel          : int   scaling reference

using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HHWiki.Extractor.Extractors;

public static class TierExtractor
{
    public sealed record TierColor(byte R, byte G, byte B, byte A)
    {
        public string HexRgb => $"#{R:X2}{G:X2}{B:X2}";
    }

    public sealed record TierConfig(
        int TierCount,
        float[] DamageFactors,
        float[] DuraFactors,
        float[] HitDownFactors,
        float[] BladeHitFactors,
        float[] RandomRates,
        TierColor[] TooltipColors,
        int MaxLootLevel,
        int MaxCraftLevel
    );

    public static TierConfig? Extract(string gameDir)
    {
        var dataDir = Path.Combine(gameDir, "Human Host_Data");
        var managedDir = Path.Combine(dataDir, "Managed");
        var levelFile = Path.Combine(dataDir, "level1");
        if (!File.Exists(levelFile))
        {
            Console.WriteLine($"[tier] missing level file: {levelFile}");
            return null;
        }

        var mgr = new AssetsManager();
        // Class-package tells AssetsTools how vanilla Unity types are laid out. Ship one alongside
        // the extractor if you have one (from the AssetRipper / AssetsTools GitHub); if not, the
        // MonoCecil generator still resolves user script fields correctly.
        var tpkCandidates = new[]
        {
            Path.Combine(managedDir, "classdata.tpk"),
            Path.Combine(AppContext.BaseDirectory, "classdata.tpk"),
        };
        foreach (var p in tpkCandidates)
        {
            if (File.Exists(p))
            {
                mgr.LoadClassPackage(p);
                break;
            }
        }
        mgr.MonoTempGenerator = new MonoCecilTempGenerator(managedDir);

        var fileInst = mgr.LoadAssetsFile(levelFile, true);
        if (mgr.ClassPackage != null)
        {
            mgr.LoadClassDatabaseFromPackage(fileInst.file.Metadata.UnityVersion);
        }

        // Enumerate every MonoBehaviour. For each, try to read its base field. If it decodes and
        // exposes _qualityDamageFactors, that's the Item_Slot_Mgr.
        var mbInfos = fileInst.file.GetAssetsOfType(AssetClassID.MonoBehaviour).ToList();
        Console.WriteLine($"[tier] scanning {mbInfos.Count} MonoBehaviours in {Path.GetFileName(levelFile)}");
        int candidatesInspected = 0;
        int decodeFailures = 0;
        var sampleFieldSets = new HashSet<string>();
        foreach (var info in mbInfos)
        {
            AssetTypeValueField root;
            try { root = mgr.GetBaseField(fileInst, info); }
            catch { decodeFailures++; continue; }
            if (root == null) { decodeFailures++; continue; }

            var damageField = root["_qualityDamageFactors"];
            if (damageField == null || damageField.IsDummy)
            {
                // Log the first few unique field-name signatures for diagnostics
                if (sampleFieldSets.Count < 10 && root.Children.Count > 5)
                {
                    var sig = string.Join(",", root.Children.Take(3).Select(c => c.FieldName));
                    if (sampleFieldSets.Add(sig)) Console.WriteLine($"[tier][sample MB fields] {sig}, ... ({root.Children.Count} total)");
                }
                continue;
            }
            candidatesInspected++;

            // We have the right MonoBehaviour. Try to read our fields; some may be absent
            // depending on script assembly resolution.
            var damage   = ReadFloatArray(root, "_qualityDamageFactors");
            var dura     = ReadFloatArray(root, "_qualityDuraFactors");
            var hitDown  = ReadFloatArray(root, "_qualityHitDownFactors");
            var bladeHit = ReadFloatArray(root, "_qualityBladeHitFactors");
            var rates    = ReadFloatArray(root, "_qualityRandomRates");
            var colors   = ReadColorArray(root, "_QualityTooltipColors");
            var maxLoot  = ReadInt(root, "_MaxLootLevel", 100);
            var maxCraft = ReadInt(root, "_MaxCraftLevel", 100);

            var tierCount = new[] { damage.Length, dura.Length, hitDown.Length, bladeHit.Length, colors.Length }
                .Where(n => n > 0).DefaultIfEmpty(0).Max();

            if (tierCount == 0)
            {
                Console.WriteLine("[tier] Item_Slot_Mgr found but all quality arrays are empty (script deserialization likely failed)");
                DumpTopLevelFields(root);
                return null;
            }

            return new TierConfig(
                TierCount: tierCount,
                DamageFactors: damage,
                DuraFactors: dura,
                HitDownFactors: hitDown,
                BladeHitFactors: bladeHit,
                RandomRates: rates,
                TooltipColors: colors,
                MaxLootLevel: maxLoot,
                MaxCraftLevel: maxCraft
            );
        }

        Console.WriteLine($"[tier] no MonoBehaviour with _qualityDamageFactors field found (inspected={candidatesInspected}, decode failures={decodeFailures}/{mbInfos.Count})");
        return null;
    }

    private static void DumpTopLevelFields(AssetTypeValueField root)
    {
        Console.WriteLine("[tier] top-level fields on the MonoBehaviour:");
        foreach (var f in root.Children)
        {
            Console.WriteLine($"    {f.TypeName}  {f.FieldName}");
        }
    }

    private static float[] ReadFloatArray(AssetTypeValueField root, string name)
    {
        var f = root[name];
        if (f == null || f.IsDummy) return Array.Empty<float>();
        // AssetsTools wraps arrays/lists as: field -> "Array" -> [size, data...]. Descend to the
        // element list before enumerating scalar values.
        var elements = ElementListOf(f);
        if (elements == null) return Array.Empty<float>();
        var arr = new List<float>(elements.Count);
        foreach (var child in elements)
        {
            arr.Add(child.AsFloat);
        }
        return arr.ToArray();
    }

    private static TierColor[] ReadColorArray(AssetTypeValueField root, string name)
    {
        var f = root[name];
        if (f == null || f.IsDummy) return Array.Empty<TierColor>();
        var elements = ElementListOf(f);
        if (elements == null) return Array.Empty<TierColor>();
        var arr = new List<TierColor>(elements.Count);
        foreach (var child in elements)
        {
            var r = child["r"].AsFloat;
            var g = child["g"].AsFloat;
            var b = child["b"].AsFloat;
            var a = child["a"].AsFloat;
            arr.Add(new TierColor(
                (byte)Math.Clamp((int)Math.Round(r * 255f), 0, 255),
                (byte)Math.Clamp((int)Math.Round(g * 255f), 0, 255),
                (byte)Math.Clamp((int)Math.Round(b * 255f), 0, 255),
                (byte)Math.Clamp((int)Math.Round(a * 255f), 0, 255)
            ));
        }
        return arr.ToArray();
    }

    // AssetsTools serializes arrays/lists as a wrapper field whose only child is named "Array";
    // that Array field has a "size" child (int) and a "data" child (the element list). Some
    // versions inline the elements as direct children of "Array". Handle both.
    private static IReadOnlyList<AssetTypeValueField>? ElementListOf(AssetTypeValueField f)
    {
        if (f == null) return null;
        // Direct children shape (e.g. loosely typed)
        if (f.Children.Count > 0 && f.Children[0].FieldName != "Array" && f.Children[0].FieldName != "size")
        {
            return f.Children;
        }
        var arrayField = f["Array"];
        if (arrayField == null || arrayField.IsDummy) arrayField = f;
        // Skip the leading "size" field when present; elements are everything after it.
        var elems = new List<AssetTypeValueField>(arrayField.Children.Count);
        foreach (var c in arrayField.Children)
        {
            if (c.FieldName == "size") continue;
            elems.Add(c);
        }
        return elems;
    }

    private static int ReadInt(AssetTypeValueField root, string name, int fallback)
    {
        var f = root[name];
        if (f == null || f.IsDummy) return fallback;
        return f.AsInt;
    }
}
