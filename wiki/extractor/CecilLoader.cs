using Mono.Cecil;

namespace HHWiki.Extractor;

/// Loads every game-authored DLL in a Managed/ folder into a shared Cecil resolver so
/// cross-assembly base-type lookups (e.g. Trap.dll -> Trap_Base) work.
public sealed class CecilLoader : IDisposable
{
    private readonly DefaultAssemblyResolver _resolver;
    private readonly List<AssemblyDefinition> _assemblies;

    public IReadOnlyList<AssemblyDefinition> Assemblies => _assemblies;

    public CecilLoader(DirectoryInfo managedDir)
    {
        _resolver = new DefaultAssemblyResolver();
        _resolver.AddSearchDirectory(managedDir.FullName);

        var readerParams = new ReaderParameters
        {
            AssemblyResolver = _resolver,
            ReadingMode = ReadingMode.Deferred,
            InMemory = true,
            ReadSymbols = false,
        };

        _assemblies = new List<AssemblyDefinition>();
        foreach (var file in managedDir.EnumerateFiles("*.dll"))
        {
            if (IsSkippable(file.Name)) continue;
            try
            {
                _assemblies.Add(AssemblyDefinition.ReadAssembly(file.FullName, readerParams));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cecil] skip {file.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"[cecil] loaded {_assemblies.Count} game assemblies");
    }

    // Skip Unity engine modules, framework, and heavy 3rd-party libs: they don't hold game-authored data.
    private static readonly string[] SkipPrefixes = {
        "unityengine.", "unity.", "system.", "mscorlib", "netstandard", "mono.",
        "bepinex", "0harmony", "microsoft.", "sentry.", "newtonsoft.",
        "gpuinstancer", "digger", "enviro", "bzkovsoft", "dotween", "finalik",
        "clipper", "cinderflame", "advancedcullingsystem", "adaptivegi", "astar",
        "boxophobic", "amazingassets", "autodesk", "hbao", "easysave", "grabbit",
        "dinofracture", "ez_"
    };

    private static bool IsSkippable(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var p in SkipPrefixes)
        {
            if (lower.StartsWith(p)) return true;
        }
        return false;
    }

    public IEnumerable<TypeDefinition> AllTypes()
    {
        foreach (var asm in _assemblies)
        foreach (var module in asm.Modules)
        foreach (var t in module.Types)
        {
            if (t.Name == "<Module>") continue;
            yield return t;
            foreach (var nested in t.NestedTypes) yield return nested;
        }
    }

    public bool DerivesFrom(TypeReference type, string baseName)
    {
        var current = type;
        var guard = 0;
        while (current != null && guard++ < 32)
        {
            if (current.Name == baseName || current.FullName == baseName) return true;
            TypeDefinition? def;
            try { def = current.Resolve(); }
            catch { return false; }
            if (def == null) return false;
            current = def.BaseType;
        }
        return false;
    }

    public void Dispose()
    {
        foreach (var a in _assemblies) a.Dispose();
        _resolver.Dispose();
    }
}
