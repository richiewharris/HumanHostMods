using System.Text.RegularExpressions;

namespace HHWiki.Extractor.Extractors;

/// Biomes are inferred from the terrain_*.bundle set: one bundle per biome variant -
/// then enriched with our known canonical name / tier / climate / threat.
public static class BiomeExtractor
{
    public sealed record Biome(
        string Slug, string PageTitle, string BundleFile,
        int Tier, string Climate, string Threat);

    private static readonly Dictionary<string, (string title, int tier, string climate, string threat)> KnownBiomes = new()
    {
        ["forest"]           = ("Forest",           1, "Temperate",           "Low"),
        ["mossy_forest"]     = ("Mossy Forest",     1, "Temperate, damp",     "Low"),
        ["mountain_forest"]  = ("Mountain Forest",  2, "Cool, alpine",        "Medium"),
        ["tropical_jungle"]  = ("Tropical Jungle",  2, "Hot, wet",            "Medium"),
        ["tropical_swamp"]   = ("Tropical Swamp",   2, "Hot, wet",            "Medium-High"),
        ["desert"]           = ("Desert",           3, "Hot, arid",           "Medium"),
        ["desert_rocky"]     = ("Desert Rocky",     3, "Hot, arid, rocky",    "Medium"),
        ["winter_forest"]    = ("Winter Forest",    3, "Cold, snowy",         "Medium"),
        ["winter_town"]      = ("Winter Town",      3, "Cold, urban",         "High"),
        ["wasteland"]        = ("Wasteland",        4, "Toxic, barren",       "High"),
        ["war_zone"]         = ("Warzone",          4, "Ruined urban",        "Extreme"),
    };

    public static List<Biome> Extract(DirectoryInfo bundleDir)
    {
        var pattern = new Regex(@"^terrain_(?<slug>.+?)_assets_all_[0-9a-f]+\.bundle$", RegexOptions.IgnoreCase);
        var results = new List<Biome>();
        foreach (var file in bundleDir.EnumerateFiles("terrain_*.bundle"))
        {
            var m = pattern.Match(file.Name);
            if (!m.Success) continue;
            var slug = m.Groups["slug"].Value.ToLowerInvariant();
            if (slug == "base") continue;

            if (KnownBiomes.TryGetValue(slug, out var k))
                results.Add(new Biome(slug, k.title, file.Name, k.tier, k.climate, k.threat));
            else
                results.Add(new Biome(slug, Title(slug), file.Name, 0, "Unknown", "Unknown"));
        }
        return results.OrderBy(b => b.Tier).ThenBy(b => b.PageTitle).ToList();
    }

    private static string Title(string slug) =>
        string.Join(" ", slug.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
