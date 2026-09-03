using System.CommandLine;
using System.Text.Json;
using HHWiki.Extractor;
using HHWiki.Extractor.Extractors;

var gameOpt = new Option<DirectoryInfo>(
    aliases: new[] { "--game", "-g" },
    description: "Human Host install directory (contains Human Host.exe)")
{
    IsRequired = true,
};
var outOpt = new Option<DirectoryInfo>(
    aliases: new[] { "--out", "-o" },
    description: "Output directory for JSON files")
{
    IsRequired = true,
};

var root = new RootCommand("Human Host wiki data extractor. Reads shipped Mono assemblies and the asset-bundle catalog, emits JSON that the wiki generator turns into MediaWiki pages.")
{
    gameOpt,
    outOpt,
};

root.SetHandler((game, outDir) =>
{
    var managedDir = new DirectoryInfo(Path.Combine(game.FullName, "Human Host_Data", "Managed"));
    var bundleDir = new DirectoryInfo(Path.Combine(game.FullName, "Human Host_Data", "StreamingAssets", "aa", "StandaloneWindows64"));
    if (!managedDir.Exists) throw new DirectoryNotFoundException($"Managed dir not found: {managedDir}");
    if (!bundleDir.Exists) throw new DirectoryNotFoundException($"AssetBundle dir not found: {bundleDir}");
    outDir.Create();

    Console.WriteLine($"[extractor] game     = {game.FullName}");
    Console.WriteLine($"[extractor] managed  = {managedDir.FullName}");
    Console.WriteLine($"[extractor] bundles  = {bundleDir.FullName}");
    Console.WriteLine($"[extractor] out      = {outDir.FullName}");
    Console.WriteLine();

    using var loader = new CecilLoader(managedDir);

    Emit(outDir, "biomes",           BiomeExtractor.Extract(bundleDir));
    Emit(outDir, "locations",        LocationExtractor.Extract(bundleDir));
    Emit(outDir, "loot_crates",      LootCrateExtractor.Extract(bundleDir));
    Emit(outDir, "vehicle_bundles",  VehicleBundleExtractor.Extract(bundleDir));
    Emit(outDir, "skill_factors",    SkillFactorExtractor.Extract(loader));
    Emit(outDir, "trap_classes",     TrapExtractor.Extract(loader));
    Emit(outDir, "weapon_classes",   WeaponExtractor.Extract(loader));
    Emit(outDir, "build_categories", BuildCategoryExtractor.Extract(loader));
    Emit(outDir, "workbench_types",  WorkbenchExtractor.Extract(loader));
    Emit(outDir, "slot_types",       SlotTypeExtractor.Extract(loader));
    Emit(outDir, "managers",         ManagerExtractor.Extract(loader));
    Emit(outDir, "resource_catalog", ResourceCatalogExtractor.Extract(loader));
    Emit(outDir, "asset_catalog",    CatalogExtractor.Extract(game));
    Emit(outDir, "recipe_shapes",    RecipeExtractor.Extract(loader));

    // Proper Unity Addressables parse: GUID -> canonical asset name map
    var catalogJson = Path.Combine(game.FullName, "Human Host_Data", "StreamingAssets", "aa", "catalog.json");
    var catRes = CatalogEntryParser.Parse(catalogJson);
    Console.WriteLine($"[extractor] catalog: keys={catRes.TotalKeys}, entries={catRes.TotalEntries}, GUID-mapped={catRes.GuidCount}");
    Emit(outDir, "guid_name_map",    catRes.GuidToName);

    // Tier / quality system (damage/dura/hitdown/blade multipliers + tooltip color codes)
    var tierConfig = TierExtractor.Extract(game.FullName);
    if (tierConfig != null)
    {
        Emit(outDir, "tier_config", tierConfig);
    }
    else
    {
        Console.WriteLine("[extractor] tier_config: extraction returned nothing (see log)");
    }

    // Perks / talents (Fight / Survive / Craft categories, per-level replacement values)
    var perks = PerkExtractor.Extract(game.FullName);
    if (perks != null)
    {
        Emit(outDir, "perks", perks);
    }
    else
    {
        Console.WriteLine("[extractor] perks: extraction returned nothing (see log)");
    }

    // HandMade recipes (crafted "by hand" in the basic crafting interface, no workbench required)
    var handCraft = HandCraftExtractor.Extract(game.FullName);
    if (handCraft != null)
    {
        Emit(outDir, "handmade_recipes", handCraft);
    }
    else
    {
        Console.WriteLine("[extractor] handmade_recipes: extraction returned nothing (see log)");
    }

    Console.WriteLine();
    Console.WriteLine("[extractor] done.");
}, gameOpt, outOpt);

return await root.InvokeAsync(args);

static void Emit(DirectoryInfo outDir, string name, object data)
{
    var path = Path.Combine(outDir.FullName, name + ".json");
    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    });
    File.WriteAllText(path, json);
    var count = data is System.Collections.ICollection c ? c.Count.ToString() : "?";
    Console.WriteLine($"[extractor] wrote {name}.json ({count} entries)");
}
