namespace HHWiki.Extractor.Extractors;

public static class SlotTypeExtractor
{
    public sealed record Slot(string Name, string Assembly);

    public static List<Slot> Extract(CecilLoader loader)
    {
        var results = new List<Slot>();
        foreach (var t in loader.AllTypes())
        {
            if (!t.IsEnum) continue;
            if (!t.Name.Equals("Slot_Type", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var f in t.Fields)
            {
                if (!f.IsStatic || f.Name.Equals("value__", StringComparison.Ordinal)) continue;
                results.Add(new Slot(f.Name, t.Module.Assembly.Name.Name));
            }
        }
        return results;
    }
}
