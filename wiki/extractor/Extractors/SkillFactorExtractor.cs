namespace HHWiki.Extractor.Extractors;

/// Every "_xxx_Factor" field on Char_Skills: the runtime multipliers that perks modify.
public static class SkillFactorExtractor
{
    public sealed record Factor(string Field, string FieldType, string Assembly, string PerkGuess);

    public static List<Factor> Extract(CecilLoader loader)
    {
        var results = new List<Factor>();
        foreach (var t in loader.AllTypes())
        {
            if (t.Name != "Char_Skills") continue;
            foreach (var f in t.Fields)
            {
                if (!f.Name.EndsWith("_Factor", StringComparison.Ordinal)) continue;
                results.Add(new Factor(
                    Field: f.Name,
                    FieldType: f.FieldType.Name,
                    Assembly: t.Module.Assembly.Name.Name,
                    PerkGuess: GuessPerkName(f.Name)));
            }
        }
        return results.OrderBy(r => r.Field).ToList();
    }

    private static string GuessPerkName(string field)
    {
        var trimmed = field.TrimStart('_');
        var idx = trimmed.LastIndexOf("_Factor", StringComparison.Ordinal);
        if (idx > 0) trimmed = trimmed[..idx];
        return trimmed.Replace('_', ' ');
    }
}
