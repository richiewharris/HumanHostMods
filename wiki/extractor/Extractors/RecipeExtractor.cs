using Mono.Cecil;

namespace HHWiki.Extractor.Extractors;

/// <summary>
/// Inspects the recipe / crafting data structures so we know:
///   - What shape recipes have (inputs, outputs, workbench, cost)
///   - Whether recipe DATA is in constant fields (extractable via Cecil) or in ScriptableObject
///     instances inside asset bundles (requires AssetRipper/UABE)
///   - The Build_Info shape (for workbench build cost)
///
/// Emits type shape info so we know the next step: if fields are all
/// [SerializeField] on a MonoBehaviour, the data lives in the bundles.
/// </summary>
public static class RecipeExtractor
{
    public sealed record TypeShape(
        string Name,
        string FullName,
        string Assembly,
        string BaseType,
        bool IsAbstract,
        bool IsMonoBehaviour,
        bool IsScriptableObject,
        List<FieldInfo> Fields);

    public sealed record FieldInfo(string Name, string TypeName, string Visibility, bool IsSerialized);

    public sealed record Result(
        List<TypeShape> CraftTypes,
        List<TypeShape> BuildInfoTypes,
        List<TypeShape> RecipeAdjacentTypes);

    // Names we want to inspect. These are the classes DESIGN.md called out plus adjacent recipe types.
    private static readonly HashSet<string> CraftTypeNames = new(StringComparer.Ordinal)
    {
        "Craft_Items", "CraftItemData", "PerIconData", "Craft_Mgr",
        "Recipe", "RecipeData", "CraftRecipe", "Craft_Data"
    };

    private static readonly HashSet<string> BuildInfoTypeNames = new(StringComparer.Ordinal)
    {
        "Build_Info", "BuildInfo", "Build_Data", "BuildData", "BuildCost", "PlacementInfo"
    };

    private static readonly string[] AdjacentHints = new[]
    {
        "Recipe", "Craft", "Build_Info", "PerIcon", "Ingredient", "CraftCost", "PlacementCost"
    };

    public static Result Extract(CecilLoader loader)
    {
        var craft = new List<TypeShape>();
        var build = new List<TypeShape>();
        var adjacent = new List<TypeShape>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var t in loader.AllTypes())
        {
            var shape = TryMakeShape(t, loader);
            if (shape == null) continue;

            if (CraftTypeNames.Contains(t.Name)) { craft.Add(shape); seen.Add(t.FullName); continue; }
            if (BuildInfoTypeNames.Contains(t.Name)) { build.Add(shape); seen.Add(t.FullName); continue; }

            if (AdjacentHints.Any(h => t.Name.Contains(h, StringComparison.OrdinalIgnoreCase)) &&
                !seen.Contains(t.FullName))
            {
                adjacent.Add(shape);
            }
        }

        return new Result(
            craft.OrderBy(x => x.Name).ToList(),
            build.OrderBy(x => x.Name).ToList(),
            adjacent.OrderBy(x => x.Name).ToList());
    }

    private static TypeShape? TryMakeShape(TypeDefinition t, CecilLoader loader)
    {
        // Fields we care about: any serialized field (public, or [SerializeField] private) - these are
        // the ones Unity persists in .asset / prefab data. Static const fields also count (compile-time
        // constants are visible to us).
        var fields = new List<FieldInfo>();
        foreach (var f in t.Fields)
        {
            var isSerialized = f.IsPublic ||
                f.CustomAttributes.Any(a => a.AttributeType.Name.Contains("SerializeField", StringComparison.Ordinal));
            fields.Add(new FieldInfo(
                Name: f.Name,
                TypeName: f.FieldType.Name,
                Visibility: f.IsPublic ? "public" : (f.IsPrivate ? "private" : "internal"),
                IsSerialized: isSerialized));
        }

        var baseName = t.BaseType?.Name ?? "";
        var isMB = loader.DerivesFrom(t.BaseType, "MonoBehaviour");
        var isSO = loader.DerivesFrom(t.BaseType, "ScriptableObject");

        return new TypeShape(
            Name: t.Name,
            FullName: t.FullName,
            Assembly: t.Module.Assembly.Name.Name,
            BaseType: baseName,
            IsAbstract: t.IsAbstract,
            IsMonoBehaviour: isMB,
            IsScriptableObject: isSO,
            Fields: fields);
    }
}
