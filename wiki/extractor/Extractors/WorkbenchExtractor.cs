namespace HHWiki.Extractor.Extractors;

public static class WorkbenchExtractor
{
    public sealed record Workbench(string Name, string EnumName, string Assembly);

    public static List<Workbench> Extract(CecilLoader loader)
    {
        var results = new List<Workbench>();
        foreach (var t in loader.AllTypes())
        {
            if (!t.IsEnum) continue;
            if (!t.Name.Contains("Workbench", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var f in t.Fields)
            {
                if (!f.IsStatic || f.Name.Equals("value__", StringComparison.Ordinal)) continue;
                results.Add(new Workbench(f.Name, t.Name, t.Module.Assembly.Name.Name));
            }
        }
        return results.OrderBy(r => r.Name).ToList();
    }
}
