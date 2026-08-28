namespace HHWiki.Extractor.Extractors;

public static class WeaponExtractor
{
    public sealed record WeaponClass(string Name, string Assembly, string BaseKind);

    private static readonly string[] BaseTypes = { "Weapon_Melee", "Weapon_Range", "Tool_Interacter" };

    public static List<WeaponClass> Extract(CecilLoader loader)
    {
        var results = new List<WeaponClass>();
        foreach (var t in loader.AllTypes())
        {
            if (t.IsAbstract || t.BaseType == null) continue;
            foreach (var baseName in BaseTypes)
            {
                if (loader.DerivesFrom(t.BaseType, baseName))
                {
                    results.Add(new WeaponClass(t.Name, t.Module.Assembly.Name.Name, baseName));
                    break;
                }
            }
        }
        return results.OrderBy(r => r.BaseKind).ThenBy(r => r.Name).ToList();
    }
}
