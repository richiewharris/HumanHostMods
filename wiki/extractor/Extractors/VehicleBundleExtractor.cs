using System.Text.RegularExpressions;

namespace HHWiki.Extractor.Extractors;

public static class VehicleBundleExtractor
{
    public sealed record VehicleGroup(string Slug, string PageTitle, string Biome, string BundleFile);

    private static readonly Regex Pattern = new(
        @"^cars_(?<biome>[a-z]+)_assets_all_[0-9a-f]+\.bundle$",
        RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> BiomeMap = new()
    {
        ["desert"]     = "Desert",
        ["mossy"]      = "Mossy Forest",
        ["mountain"]   = "Mountain Forest",
        ["rainforest"] = "Tropical Jungle",
        ["snowy"]      = "Winter Forest",
        ["warzone"]    = "Warzone",
    };

    public static List<VehicleGroup> Extract(DirectoryInfo bundleDir)
    {
        var results = new List<VehicleGroup>();
        foreach (var file in bundleDir.EnumerateFiles("cars_*.bundle"))
        {
            var m = Pattern.Match(file.Name);
            if (!m.Success) continue;
            var key = m.Groups["biome"].Value.ToLowerInvariant();
            var biome = BiomeMap.TryGetValue(key, out var b) ? b : key;
            results.Add(new VehicleGroup(key, $"{biome} Vehicles", biome, file.Name));
        }
        return results.OrderBy(r => r.Biome).ToList();
    }
}
