using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Solver;

/// <summary>
/// Main-thread owner of the current <see cref="CombatUiSnapshot"/>. The UI reads the immutable
/// reference; only the game main thread publishes. Search workers never touch this store.
/// </summary>
internal static class CombatUiStore
{
    private static readonly object Gate = new();
    private static CombatUiSnapshot _snapshot = new();
    private static long _sequence;
    private static bool _initialized;

    internal static CombatUiSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal static bool Initialized => Volatile.Read(ref _initialized);

    internal static void Reset(CombatUiSnapshot snapshot)
    {
        RequireMainThread();
        lock (Gate)
        {
            _snapshot = snapshot with { Sequence = ++_sequence };
            _initialized = true;
#if STS2_SOLVER_TESTING
            HistoryBuffer.Clear();
#endif
        }
    }

    internal static void Publish(CombatUiSnapshot snapshot)
    {
        RequireMainThread();
        lock (Gate)
            PublishLocked(snapshot);
    }

    internal static void Update(Func<CombatUiSnapshot, CombatUiSnapshot> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        RequireMainThread();
        lock (Gate)
            PublishLocked(change(_snapshot));
    }

    /// <summary>Samples process memory and the effective search budget; called by the 4 Hz Mod node.</summary>
    internal static void SampleMemory()
    {
        RequireMainThread();
        SearchMemoryBudgetResolution budget = SearchMemoryBudget.Resolve();
        MemoryUiSnapshot memory = CombatUiProjection.Memory(budget);
        SettingsUiSnapshot settings = CombatUiProjection.Settings(budget);
        lock (Gate)
        {
            PublishLocked(_snapshot with
            {
                Memory = memory,
                Settings = settings,
            });
        }
    }

    internal static void OnSettingsChanged()
    {
        if (!_initialized || !NGame.IsMainThread())
            return;
        SearchMemoryBudgetResolution budget = SearchMemoryBudget.Resolve();
        SettingsUiSnapshot settings = CombatUiProjection.Settings(budget);
        Update(snapshot => snapshot with
        {
            Settings = settings,
            Memory = snapshot.Memory with
            {
                BudgetBytes = budget.Bytes,
                BudgetSource = budget.Source,
            },
        });
    }

#if STS2_SOLVER_TESTING
    private const int MaximumHistory = 4096;

    private static readonly List<CombatUiSnapshot> HistoryBuffer = [];
    private static bool _recordHistory;

    internal static bool RecordHistory
    {
        get
        {
            lock (Gate)
                return _recordHistory;
        }
        set
        {
            lock (Gate)
            {
                _recordHistory = value;
                if (!value)
                    HistoryBuffer.Clear();
            }
        }
    }

    internal static CombatUiSnapshot[] History
    {
        get
        {
            lock (Gate)
                return [.. HistoryBuffer];
        }
    }

    internal static void ClearHistory()
    {
        lock (Gate)
            HistoryBuffer.Clear();
    }
#endif

    private static void PublishLocked(CombatUiSnapshot snapshot)
    {
        CombatUiSnapshot published = snapshot with { Sequence = ++_sequence };
        Volatile.Write(ref _snapshot, published);
#if STS2_SOLVER_TESTING
        if (_recordHistory)
        {
            HistoryBuffer.Add(published);
            if (HistoryBuffer.Count > MaximumHistory)
                HistoryBuffer.RemoveAt(0);
        }
#endif
    }

    private static void RequireMainThread()
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("UI 快照只能在游戏主线程发布。");
    }
}
