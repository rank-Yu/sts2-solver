using Godot;

namespace Sts2Solver;

/// <summary>
/// Mod-side owner of the one persisted settings file. The store itself is Godot-free; this wrapper
/// only resolves the Godot <c>user://</c> path and forwards stable log events.
/// </summary>
internal static class SolverSettings
{
    internal const float MinimumOverlayWidth = 400f;

    internal const float MinimumOverlayHeight = 300f;

    internal const string RelativePath = "user://sts2solver/settings.json";

    private static SolverSettingsStore? _store;

    internal static SolverSettingsData Current => _store?.Current ?? new SolverSettingsData();

    internal static SolverSettingsStore Store
        => _store ?? throw new InvalidOperationException("设置存储尚未初始化。");

    internal static string? FilePath { get; private set; }

    internal static void Initialize()
    {
        if (_store != null)
            return;
        string path = ProjectSettings.GlobalizePath(RelativePath);
        FilePath = path;
        _store = SolverSettingsStore.Load(path, ModLog.Info, ModLog.Error);
        ModLog.Info($"[Sts2Solver] SETTINGS_PATH path={path}");
    }

    internal static bool Update(Func<SolverSettingsData, SolverSettingsData> change)
    {
        if (_store == null)
        {
            ModLog.Error("[Sts2Solver] SETTINGS_NOT_INITIALIZED");
            return false;
        }
        if (!_store.Update(change, out string? error))
        {
            ModLog.Error($"[Sts2Solver] SETTINGS_UPDATE_FAILED reason={error ?? "unknown"}");
            return false;
        }
        CombatUiStore.OnSettingsChanged();
        return true;
    }
}
