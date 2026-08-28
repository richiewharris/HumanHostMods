using System.Text.RegularExpressions;

namespace HHWiki.Extractor.Extractors;

/// POIs from sh_*.bundle files. sh_poi_<biome> aggregate packs are skipped
/// (represented at the biome level, not as their own pages).
public static class LocationExtractor
{
    public sealed record Location(string Slug, string PageTitle, string Biome, string BundleFile);

    // Note: "warzone" must precede "war" in the alternation so it wins the longest match.
    private static readonly Regex Named = new(
        @"^sh_(?<biome>warzone|forest|mountain|mossy|desert|snowy|swamp|winter|war|rainforest)(?:_ab)?_(?<name>[a-z0-9_]+?)_assets_all_[0-9a-f]+\.bundle$",
        RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> TitleOverrides = new()
    {
        ["airport"]        = "Airport",
        ["mall"]           = "Mall",
        ["pool"]           = "Pool",
        ["school_1"]       = "School",
        ["dinner"]         = "Diner",
        ["countryhouse"]   = "Country House",
        ["countryhouse2"]  = "Country House II",
        ["av_house"]       = "AV House",
        ["factoryzone"]    = "Factory Zone",
        ["buildings"]      = "Warzone Buildings",
    };

    // The game's "forest" POI prefix is a generic tag for starter-biome POIs. Map it to
    // Mossy Forest (the sole starting biome that survives in the bundle catalog).
    private static readonly Dictionary<string, string> BiomeMap = new()
    {
        ["forest"]     = "Mossy Forest",
        ["mossy"]      = "Mossy Forest",
        ["mountain"]   = "Mountain Forest",
        ["desert"]     = "Desert",
        ["snowy"]      = "Winter Forest",
        ["swamp"]      = "Tropical Swamp",
        ["winter"]     = "Winter Forest",
        ["war"]        = "Warzone",
        ["warzone"]    = "Warzone",
        ["rainforest"] = "Tropical Jungle",
    };

    public static List<Location> Extract(DirectoryInfo bundleDir)
    {
        var results = new List<Location>();
        foreach (var file in bundleDir.EnumerateFiles("sh_*.bundle"))
        {
            if (file.Name.StartsWith("sh_poi_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (file.Name.StartsWith("sh_cbu_", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new Location("cbu_building_pack", "CBU Building Pack", "", file.Name));
                continue;
            }
            var m = Named.Match(file.Name);
            if (!m.Success) continue;

            var slug = m.Groups["name"].Value.ToLowerInvariant();
            var biomeKey = m.Groups["biome"].Value.ToLowerInvariant();
            var title = TitleOverrides.TryGetValue(slug, out var t) ? t : Title(slug);
            var biome = BiomeMap.TryGetValue(biomeKey, out var b) ? b : Title(biomeKey);
            results.Add(new Location($"{biomeKey}_{slug}", title, biome, file.Name));
        }
        return results.OrderBy(r => r.Biome).ThenBy(r => r.PageTitle).ToList();
    }

    private static string Title(string slug) =>
        string.Join(" ", slug.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
