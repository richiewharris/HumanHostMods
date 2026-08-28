using System.Text.RegularExpressions;

namespace HHWiki.Extractor.Extractors;

public static class LootCrateExtractor
{
    public sealed record LootCrate(string Slug, string PageTitle, string Biome, string BundleFile);

    private static readonly Regex Pattern = new(
        @"^shc_crates_(?<biome>[a-z]+)_assets_all_[0-9a-f]+\.bundle$",
        RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, (string title, string biome)> BiomeMap = new()
    {
        ["desert"]   = ("Desert Loot Crate",     "Desert"),
        ["mossy"]    = ("Mossy Loot Crate",      "Mossy Forest"),
        ["mountain"] = ("Mountain Loot Crate",   "Mountain Forest"),
        ["snowy"]    = ("Snowy Loot Crate",      "Winter Forest"),
        ["swamp"]    = ("Swamp Loot Crate",      "Tropical Swamp"),
        ["warzone"]  = ("Warzone Loot Crate",    "Warzone"),
    };

    public static List<LootCrate> Extract(DirectoryInfo bundleDir)
    {
        var results = new List<LootCrate>();
        foreach (var file in bundleDir.EnumerateFiles("shc_crates_*.bundle"))
        {
            var m = Pattern.Match(file.Name);
            if (!m.Success) continue;
            var key = m.Groups["biome"].Value.ToLowerInvariant();
            var (title, biome) = BiomeMap.TryGetValue(key, out var v)
                ? v
                : ($"{Cap(key)} Loot Crate", Cap(key));
            results.Add(new LootCrate(key, title, biome, file.Name));
        }
        return results.OrderBy(r => r.PageTitle).ToList();
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
