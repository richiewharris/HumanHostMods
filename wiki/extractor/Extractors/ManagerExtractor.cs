namespace HHWiki.Extractor.Extractors;

/// Every game-authored singleton manager (public static "_ins"/"ins"/"Instance" field).
/// Filters out third-party pool/observer types (Cysharp, Animancer, Pathfinding) that pollute results.
public static class ManagerExtractor
{
    public sealed record Manager(string Name, string FullName, string Assembly);

    private static readonly string[] NoiseNamespacePrefixes =
    {
        "Cysharp.", "Animancer", "Pathfinding.", "MeshFusionPro.", "Kybernetik.",
    };

    private static readonly string[] NoiseAssemblies =
    {
        "UniTask", "UniTask.Linq", "UniTask.TextMeshPro",
        "Kybernetik.Animancer", "Kybernetik.Animancer.FSM",
        "Pathfinding.Ionic.Zip.Reduced", "MeshFusionPro.Runtime",
    };

    public static List<Manager> Extract(CecilLoader loader)
    {
        var results = new List<Manager>();
        foreach (var t in loader.AllTypes())
        {
            var hasIns = t.Fields.Any(f => f.IsStatic && (f.Name == "_ins" || f.Name == "ins" || f.Name == "Instance"));
            if (!hasIns) continue;

            var asm = t.Module.Assembly.Name.Name;
            if (NoiseAssemblies.Contains(asm)) continue;
            if (NoiseNamespacePrefixes.Any(p => t.FullName.StartsWith(p, StringComparison.Ordinal))) continue;
            // Skip generic types (their FullName includes `1, `2 etc: mostly pool internals)
            if (t.HasGenericParameters) continue;

            results.Add(new Manager(t.Name, t.FullName, asm));
        }
        return results.OrderBy(r => r.Name).ToList();
    }
}
