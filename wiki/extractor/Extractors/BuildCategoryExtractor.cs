namespace HHWiki.Extractor.Extractors;

public static class BuildCategoryExtractor
{
    public sealed record EnumEntry(string EnumName, string Assembly, List<string> Values);

    private static readonly string[] EnumHints = { "BuildCategory", "Build_Category", "BuildType", "Build_Type" };

    public static List<EnumEntry> Extract(CecilLoader loader)
    {
        var results = new List<EnumEntry>();
        foreach (var t in loader.AllTypes())
        {
            if (!t.IsEnum) continue;
            var match = EnumHints.Any(h => t.Name.Contains(h, StringComparison.OrdinalIgnoreCase));
            if (!match) continue;
            var values = t.Fields
                .Where(f => f.IsStatic && !f.Name.Equals("value__", StringComparison.Ordinal))
                .Select(f => f.Name)
                .ToList();
            if (values.Count == 0) continue;
            results.Add(new EnumEntry(t.Name, t.Module.Assembly.Name.Name, values));
        }
        return results;
    }
}
