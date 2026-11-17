using System.Reflection;
using System.Runtime.Loader;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2Solver.Loader;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public static void Initialize()
    {
        string version = ReleaseInfoManager.Instance.ReleaseInfo!.Version;
        string target = RuntimeTarget.ForGameVersion(version);
        Mod mod = ModManager.Mods.Single(m => m.manifest?.id == "Sts2Solver");
        Assembly loader = typeof(Entry).Assembly;
        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(loader)!;
        Assembly runtime = context.LoadFromAssemblyPath(
            Path.GetFullPath(Path.Combine(mod.path, target, "Sts2Solver.Runtime.dll")));
        mod.assemblies.Add(runtime);
        Log.Info($"[Sts2Solver] LOAD_RUNTIME game={version} target={target} version={runtime.GetName().Version}");
        Action initialize = runtime.GetType("Sts2Solver.ModEntry", throwOnError: true)!
            .GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Action>();
        initialize();
    }
}
