using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Sts2Solver.Architecture;

/// <summary>直接读发布 DLL 元数据；无需加载游戏程序集，也不会触发游戏初始化。</summary>
internal static class ModBoundary
{
    internal static int Check(string path, bool testing)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader pe = new(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        List<string> types = [];
        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            types.Add(metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name));
        }
        string[] forbidden = ["Sts2Solver.Simulation.Run.", "Sts2Solver.Ai.Run.",
            "Sts2Solver.OfflineHost.", "Sts2Solver.Autoplay.RunPilot"];
        List<string> errors = types.Where(t => forbidden.Any(p => t.StartsWith(p, StringComparison.Ordinal))).ToList();
        string[] testTypes = ["Sts2Solver.TestSession", "Sts2Solver.GodotCombatWaiter",
            "Sts2Solver.TurnSetupRootCapturePatch", "Sts2Solver.UnattendedTestIsolationPatch",
            "Sts2Solver.UnattendedNoDiscoveryOrderPatch", "Sts2Solver.UnattendedHeadlessFtuePatch"];
        if (testing)
            errors.AddRange(testTypes.Where(t => !types.Contains(t)).Select(t => "missing " + t));
        else
            errors.AddRange(types.Where(t => t.StartsWith("Sts2Solver.TestHost.", StringComparison.Ordinal)
                || testTypes.Contains(t)));
        foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
        {
            string name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            if (name.StartsWith("Sts2Solver", StringComparison.Ordinal))
                errors.Add("external solver assembly " + name);
        }
        if (!types.Contains("Sts2Solver.CombatSessionController") || !types.Contains("Sts2Solver.ModEntry"))
            errors.Add("missing production combat entry");
        foreach (string error in errors)
            Console.Error.WriteLine("MOD_BOUNDARY_FAILED " + error);
        if (errors.Count != 0) return 1;
        Console.WriteLine($"MOD_BOUNDARY_OK testing={testing} types={types.Count} path={path}");
        return 0;
    }
}
