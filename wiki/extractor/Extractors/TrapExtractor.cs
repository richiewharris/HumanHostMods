namespace HHWiki.Extractor.Extractors;

public static class TrapExtractor
{
    public sealed record TrapClass(string Name, string FullName, string Assembly, string PageTitle);

    public static List<TrapClass> Extract(CecilLoader loader)
    {
        var results = new List<TrapClass>();
        foreach (var t in loader.AllTypes())
        {
            if (t.IsAbstract || t.BaseType == null) continue;
            if (!loader.DerivesFrom(t.BaseType, "Trap_Base")) continue;

            results.Add(new TrapClass(t.Name, t.FullName, t.Module.Assembly.Name.Name, Prettify(t.Name)));
        }
        return results.OrderBy(r => r.Name).ToList();
    }

    private static string Prettify(string className)
    {
        var n = className;
        if (n.StartsWith("Trap_", StringComparison.Ordinal)) n = n[5..];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n.Length; i++)
        {
            if (i > 0 && char.IsUpper(n[i]) && !char.IsUpper(n[i - 1])) sb.Append(' ');
            sb.Append(n[i]);
        }
        return sb.ToString().Replace('_', ' ');
    }
}
