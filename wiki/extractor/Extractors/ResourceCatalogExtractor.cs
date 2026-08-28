using Mono.Cecil;

namespace HHWiki.Extractor.Extractors;

/// <summary>
/// Surfaces the game's built-in resource-catalog enums and static string tables. Human Host
/// authors resource ids as enum values and as static readonly string fields; both are useful for
/// wiki pages that need canonical in-game names.
///
/// Emits three catalogs:
///   - Enums whose name contains 'Ore', 'Res', 'Item', 'Food', 'Water', 'Ammo', 'Mat', 'Type'
///   - Public static string fields whose name or value resembles a resource id
///   - Sound_Mat subclasses (Skill_Mgr._MinerSoundMats is a Sound_Mat[] catalog)
/// </summary>
public static class ResourceCatalogExtractor
{
    public sealed record EnumCatalog(string EnumName, string Assembly, List<string> Values);
    public sealed record StringConst(string TypeName, string FieldName, string Value, string Assembly);
    public sealed record SoundMatType(string Name, string FullName, string Assembly, List<string> Fields);

    public sealed record Result(
        List<EnumCatalog> Enums,
        List<StringConst> Strings,
        List<SoundMatType> SoundMats);

    private static readonly string[] EnumNameHints = {
        "Ore", "Res", "Resource", "Item", "Food", "Water", "Ammo", "Mat", "Fuel", "Cook"
    };

    private static readonly string[] StringNameHints = {
        "Ore", "Water", "Food", "Ammo", "Fuel", "Cook", "Bottle", "Cactus", "Mushroom",
        "PineCone", "Pine_Cone", "Ingot", "Coal", "Iron", "Copper", "Tin", "Steel", "Gunpowder"
    };

    public static Result Extract(CecilLoader loader)
    {
        var enums = new List<EnumCatalog>();
        var strings = new List<StringConst>();
        var soundMats = new List<SoundMatType>();

        foreach (var t in loader.AllTypes())
        {
            // Enums with resource-like names
            if (t.IsEnum && EnumNameHints.Any(h => t.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                var values = t.Fields
                    .Where(f => f.IsStatic && !f.Name.Equals("value__", StringComparison.Ordinal))
                    .Select(f => f.Name)
                    .ToList();
                if (values.Count > 0)
                    enums.Add(new EnumCatalog(t.Name, t.Module.Assembly.Name.Name, values));
            }

            // Static readonly / const strings whose name or value looks like a resource id
            foreach (var f in t.Fields)
            {
                if (!f.IsStatic || !f.HasConstant) continue;
                if (f.FieldType.FullName != "System.String") continue;
                var val = f.Constant as string;
                if (string.IsNullOrEmpty(val)) continue;
                var interesting = StringNameHints.Any(h =>
                    f.Name.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                    val.Contains(h, StringComparison.OrdinalIgnoreCase));
                if (interesting)
                {
                    strings.Add(new StringConst(t.Name, f.Name, val, t.Module.Assembly.Name.Name));
                }
            }

            // Sound_Mat: the base class the ore/rock catalog uses
            if (t.Name.Equals("Sound_Mat", StringComparison.Ordinal) ||
                (t.BaseType != null && loader.DerivesFrom(t.BaseType, "Sound_Mat")))
            {
                var fields = t.Fields
                    .Where(f => !f.IsPrivate || f.CustomAttributes.Any(a => a.AttributeType.Name.Contains("SerializeField")))
                    .Select(f => $"{f.Name}: {f.FieldType.Name}")
                    .ToList();
                soundMats.Add(new SoundMatType(t.Name, t.FullName, t.Module.Assembly.Name.Name, fields));
            }
        }

        return new Result(
            enums.OrderBy(e => e.EnumName).ToList(),
            strings.OrderBy(s => s.TypeName).ThenBy(s => s.FieldName).ToList(),
            soundMats.OrderBy(s => s.Name).ToList());
    }
}
