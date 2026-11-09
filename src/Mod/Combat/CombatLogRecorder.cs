// Per-combat diagnostic log. Each single-player combat gets a folder under
// user://sts2solver/combat-logs holding combat.json, every search's request (SREQ), selected result
// (SRES) and member summary, the predicted and actual states of each deployment deviation, and the
// [Sts2Solver] log lines. The main thread only encodes and enqueues; one background writer owns all
// file I/O, so a slow or failing disk never stalls the game or a search. The settings page exports
// the two newest combats as a zip the player can send; Golden replay-log reruns the searches.
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver;

/// <summary>Result of one export: the zip and its folder, or a stable error code.</summary>
internal sealed record CombatLogExport(string? ZipPath, string? Error);

internal static class CombatLogRecorder
{
    internal const string RelativeRoot = "user://sts2solver/combat-logs";
    internal const string ExportsFolder = "exports";
    internal const int SchemaVersion = 1;
    internal const int KeptCombats = 20;
    internal const int KeptExports = 10;
    internal const int ExportedCombats = 2;

    private static readonly Channel<System.Action> Queue = Channel.CreateUnbounded<System.Action>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static string? _root;
    private static string? _current;
    private static int _searches;
    private static int _deviations;
    private static int _writeFailureReported;

    /// <summary>Searches whose request <see cref="BeginSearch"/> already saved, by the compiled case.</summary>
    private static readonly ConditionalWeakTable<SearchCase, BegunSearch> Begun = new();

    private sealed record BegunSearch(int Index, string? EncodeError);

    /// <summary>Resolves the log folder and starts the writer. Test mode passes its result folder.</summary>
    internal static void Initialize(string? rootOverride)
    {
        if (_root != null)
            return;
        _root = rootOverride ?? ProjectSettings.GlobalizePath(RelativeRoot);
        _ = Task.Run(WriteLoopAsync).ContinueWith(static writer => MegaCrit.Sts2.Core.Logging.Log.Error(
                $"[Sts2Solver] COMBAT_LOG_WRITER_STOPPED reason={writer.Exception?.GetBaseException()}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    internal static string? CurrentFolderForTest => Volatile.Read(ref _current);

    /// <summary>Opens the folder of a new single-player combat and prunes the oldest ones.</summary>
    internal static void BeginCombat(CombatState state, Player player)
    {
        if (_root is not { } root)
            return;
        string encounter = state.Encounter?.Id.Entry ?? "COMBAT";
        string folder = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{SafeName(encounter)}");
        RunState? run = state.RunState as RunState;
        CombatLogInfo info = new(
            SchemaVersion,
            ModEntry.Version,
            ReleaseInfoManager.Instance.ReleaseInfo?.Version,
            DateTime.UtcNow.ToString("O"),
            player.Character?.Id.Entry,
            encounter,
            state.Encounter?.RoomType.ToString(),
            run?.CurrentActIndex,
            run?.TotalFloor,
            run?.AscensionLevel,
            SolverText.IsEnglish ? "en" : "zh");
        Volatile.Write(ref _current, folder);
        Interlocked.Exchange(ref _searches, 0);
        Interlocked.Exchange(ref _deviations, 0);
        Interlocked.Exchange(ref _writeFailureReported, 0);
        Enqueue(() =>
        {
            Directory.CreateDirectory(folder);
            WriteJson(Path.Combine(folder, "combat.json"), info);
            PruneCombats(root, folder);
        });
    }

    /// <summary>Appends one [Sts2Solver] log line to the current combat's events.log.</summary>
    internal static void AppendLine(string level, string message)
    {
        if (Volatile.Read(ref _current) is not { } folder)
            return;
        string line = $"{DateTime.UtcNow:O} {level} {message}\n";
        Enqueue(() => File.AppendAllText(Path.Combine(folder, "events.log"), line));
    }

    /// <summary>
    /// Saves a search's request and plan as soon as it is compiled, with status <c>Running</c>. A search
    /// the game cancels before it finishes (the combat ends, the player moves on) never reaches
    /// <see cref="RecordSearch"/>; its request still replays. <see cref="RecordSearch"/> completes the
    /// same entry.
    /// </summary>
    internal static void BeginSearch(SearchCase searchCase, SearchPlan plan, bool setup, int turn,
        string reason, bool resumed, SearchPerformanceTier tier, int threads)
    {
        if (Volatile.Read(ref _current) is not { } folder)
            return;
        int index = Interlocked.Increment(ref _searches);
        byte[]? request = EncodeRequest(searchCase, index, out string? encodeError);
        Begun.AddOrUpdate(searchCase, new BegunSearch(index, encodeError));
        CombatSearchLog entry = CombatSearchLog.Started(index, setup, turn, reason, resumed, tier, threads,
            plan, encodeError);
        string stem = Path.Combine(folder, $"search-{index:D3}");
        Enqueue(() =>
        {
            if (request != null)
                WriteCompressed(stem + ".sreq.br", request);
            WriteJson(stem + ".json", entry);
        });
    }

    private static byte[]? EncodeRequest(SearchCase searchCase, int index, out string? encodeError)
    {
        encodeError = null;
        byte[]? request = null;
        try
        {
            request = SearchCodec.EncodeRequest(searchCase);
        }
        catch (InvalidDataException error)
        {
            encodeError = error.Message;
        }
        catch (InvalidOperationException error)
        {
            encodeError = error.Message;
        }
        if (encodeError != null)
            ModLog.Error($"[Sts2Solver] COMBAT_LOG_ENCODE_FAILED search={index} reason={encodeError}");
        return request;
    }

    /// <summary>Saves one finished search: its request, the selected result and the member summary.</summary>
    internal static void RecordSearch(CombatSearchRun run, bool setup, int turn, string reason, bool resumed)
    {
        if (Volatile.Read(ref _current) is not { } folder)
            return;
        int index;
        byte[]? request = null;
        string? encodeError = null;
        if (run.Case is { } begunCase && Begun.TryGetValue(begunCase, out BegunSearch? begun))
        {
            // BeginSearch already wrote the request; this completes its entry.
            Begun.Remove(begunCase);
            index = begun.Index;
            encodeError = begun.EncodeError;
        }
        else
        {
            index = Interlocked.Increment(ref _searches);
            if (run.Case is { } searchCase)
                request = EncodeRequest(searchCase, index, out encodeError);
        }
        BeamResult? beam = run.Outcome.Beam;
        byte[]? result = beam == null ? null : SearchCodec.EncodeResult(beam);
        CombatSearchLog entry = CombatSearchLog.From(run, index, setup, turn, reason, resumed, encodeError);
        string stem = Path.Combine(folder, $"search-{index:D3}");
        Enqueue(() =>
        {
            if (request != null)
                WriteCompressed(stem + ".sreq.br", request);
            if (result != null)
                WriteCompressed(stem + ".sres.br", result);
            WriteJson(stem + ".json", entry);
        });
    }

    /// <summary>Saves the predicted and actual states of one deployment or boundary deviation.</summary>
    internal static void RecordDeviation(string kind, int turn, int? actionIndex,
        IReadOnlyList<string> diff, in Battle predicted, in Battle actual)
    {
        if (Volatile.Read(ref _current) is not { } folder)
            return;
        int index = Interlocked.Increment(ref _deviations);
        byte[] predictedBytes = BattleCodec.Encode(predicted);
        byte[] actualBytes = BattleCodec.Encode(actual);
        CombatDeviationLog entry = new(SchemaVersion, index, kind, turn, actionIndex,
            Volatile.Read(ref _searches), [.. diff]);
        string stem = Path.Combine(folder, $"deviation-{index:D2}");
        Enqueue(() =>
        {
            WriteCompressed(stem + ".predicted.batl.br", predictedBytes);
            WriteCompressed(stem + ".actual.batl.br", actualBytes);
            WriteJson(stem + ".json", entry);
        });
    }

    /// <summary>
    /// Zips the two newest combat folders after every earlier write has finished. Completes with the
    /// zip path, or with <c>empty</c>/<c>unavailable</c>/an I/O message.
    /// </summary>
    internal static Task<CombatLogExport> ExportAsync()
    {
        if (_root is not { } root)
            return Task.FromResult(new CombatLogExport(null, "unavailable"));
        TaskCompletionSource<CombatLogExport> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            // An unexpected exception still stops the writer (and is logged above); the page
            // gets a failed export instead of waiting forever.
            try
            {
                done.SetResult(WriteExport(root));
            }
            finally
            {
                done.TrySetResult(new CombatLogExport(null, "writer_failed"));
            }
        });
        return done.Task;
    }

    private static CombatLogExport WriteExport(string root)
    {
        try
        {
            string[] combats = CombatFolders(root);
            if (combats.Length == 0)
                return new CombatLogExport(null, "empty");
            string exports = Path.Combine(root, ExportsFolder);
            Directory.CreateDirectory(exports);
            string zip = Path.Combine(exports, $"sts2solver-combat-log-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            string temporary = zip + ".tmp";
            using (FileStream file = new(temporary, FileMode.Create, System.IO.FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new(file, ZipArchiveMode.Create))
            {
                foreach (string combat in combats.TakeLast(ExportedCombats))
                {
                    string name = Path.GetFileName(combat);
                    foreach (string path in Directory.EnumerateFiles(combat).Order(StringComparer.Ordinal))
                        archive.CreateEntryFromFile(path, $"{name}/{Path.GetFileName(path)}", CompressionLevel.Optimal);
                }
            }
            File.Move(temporary, zip, overwrite: true);
            PruneExports(exports);
            return new CombatLogExport(zip, null);
        }
        catch (IOException error)
        {
            return new CombatLogExport(null, error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return new CombatLogExport(null, error.Message);
        }
    }

    private static void Enqueue(System.Action item)
    {
        if (!Queue.Writer.TryWrite(item))
            throw new InvalidOperationException("战斗日志队列已关闭。");
    }

    private static async Task WriteLoopAsync()
    {
        await foreach (System.Action item in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                item();
            }
            catch (IOException error)
            {
                ReportWriteFailure(error);
            }
            catch (UnauthorizedAccessException error)
            {
                ReportWriteFailure(error);
            }
        }
    }

    // The game log only: ModLog would feed the failure back into events.log on the same disk.
    private static void ReportWriteFailure(Exception error)
    {
        if (Interlocked.Exchange(ref _writeFailureReported, 1) == 0)
        {
            MegaCrit.Sts2.Core.Logging.Log.Warn(
                $"[Sts2Solver] COMBAT_LOG_WRITE_FAILED kind={error.GetType().Name} reason={error.Message}");
        }
    }

    private static string[] CombatFolders(string root)
        => Directory.Exists(root)
            ? [.. Directory.EnumerateDirectories(root)
                .Where(static path => Path.GetFileName(path) != ExportsFolder)
                .Order(StringComparer.Ordinal)]
            : [];

    private static void PruneCombats(string root, string current)
    {
        string[] combats = CombatFolders(root);
        foreach (string old in combats.Take(Math.Max(0, combats.Length - KeptCombats)))
        {
            if (old != current)
                Directory.Delete(old, recursive: true);
        }
    }

    private static void PruneExports(string exports)
    {
        string[] zips = [.. Directory.EnumerateFiles(exports, "*.zip").Order(StringComparer.Ordinal)];
        foreach (string old in zips.Take(Math.Max(0, zips.Length - KeptExports)))
            File.Delete(old);
    }

    private static void WriteCompressed(string path, byte[] bytes)
    {
        using FileStream file = new(path, FileMode.Create, System.IO.FileAccess.Write, FileShare.None);
        using BrotliStream brotli = new(file, CompressionLevel.Optimal);
        brotli.Write(bytes);
    }

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + "\n");

    private static string SafeName(string text)
    {
        char[] chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] is not ('_' or '-'))
                chars[i] = '_';
        }
        return new string(chars);
    }
}

internal sealed record CombatLogInfo(int SchemaVersion, string ModVersion, string? GameVersion,
    string StartedAtUtc, string? Character, string Encounter, string? RoomType, int? ActIndex,
    int? Floor, int? Ascension, string Language);

internal sealed record CombatSearchPlanLog(int BaselineWidth, int NarrowWidth, int WideWidth,
    int OffensiveWidth, long MemoryBytes, int MillisecondsPerWidth, int NodesPerWidth,
    bool QueenStrategies, bool SetupStrategies, bool ContinuousSetupStrategies)
{
    internal static CombatSearchPlanLog From(SearchPlan plan)
        => new(plan.BaselineWidth, plan.NarrowWidth, plan.WideWidth, plan.OffensiveWidth,
            plan.MemoryBytes, plan.MillisecondsPerWidth, plan.NodesPerWidth, plan.QueenStrategies,
            plan.SetupStrategies, plan.ContinuousSetupStrategies);
}

/// <summary>
/// One portfolio member. <see cref="DeadlinePoll"/> comes from the member's own measurement: the number
/// of the deadline poll that stopped it, 0 when the deadline never expired, so the replay can stop there.
/// </summary>
internal sealed record CombatSearchMemberLog(int Width, string Evaluation, string Retention,
    string Potions, bool Ran, string? Skipped, long ElapsedMilliseconds, long ExpandedNodes,
    long? DeadlinePoll, string StopReason, bool Victory, int? HpLost, int? PotionUses, bool Adopted)
{
    internal static CombatSearchMemberLog[] From(SearchMemberResult[] members, SearchMemberRunUi[] runs)
    {
        CombatSearchMemberLog[] logs = new CombatSearchMemberLog[members.Length];
        int run = 0;
        for (int i = 0; i < members.Length; i++)
        {
            SearchMemberResult member = members[i];
            long? deadlinePoll = member.Ran && run < runs.Length ? runs[run++].DeadlineExpiredAtPoll : null;
            logs[i] = new(member.Member.Width, member.Member.Evaluation.ToString(),
                member.Member.Retention.ToString(), member.Member.Potions.ToString(), member.Ran,
                member.SkippedReason, member.ElapsedMilliseconds, member.ExpandedNodes, deadlinePoll,
                member.StopReason, member.Victory, member.HpLost, member.PotionUses, member.Adopted);
        }
        return logs;
    }
}

internal sealed record CombatSearchLog(int SchemaVersion, int Index, string Kind, int Turn,
    string Reason, bool Resumed, string Tier, int Threads, CombatSearchPlanLog? Plan, string Status,
    string Completion, int SelectedMember, long ElapsedMilliseconds, long ExpandedNodes, bool? Victory,
    double? Score, int? BestRootIndex, string[] Route, string? ErrorCode, string? ErrorDetail,
    string? EncodeError, CombatSearchMemberLog[] Members)
{
    /// <summary>The entry written when a search starts; status and completion stay <c>Running</c> until it finishes.</summary>
    internal static CombatSearchLog Started(int index, bool setup, int turn, string reason, bool resumed,
        SearchPerformanceTier tier, int threads, SearchPlan plan, string? encodeError)
        => new(CombatLogRecorder.SchemaVersion, index, setup ? "setup" : "play", turn, reason, resumed,
            tier.ToString(), threads, CombatSearchPlanLog.From(plan), "Running", "Running", -1, 0, 0,
            null, null, null, [], null, null, encodeError, []);

    internal static CombatSearchLog From(CombatSearchRun run, int index, bool setup, int turn,
        string reason, bool resumed, string? encodeError)
    {
        CombatSearchResult outcome = run.Outcome;
        BeamResult? beam = outcome.Beam;
        return new CombatSearchLog(CombatLogRecorder.SchemaVersion, index, setup ? "setup" : "play",
            turn, reason, resumed, run.Tier.ToString(), run.SearchThreads,
            run.RequestPlan is { } plan ? CombatSearchPlanLog.From(plan) : null,
            outcome.Status.ToString(), outcome.Completion.ToString(), outcome.SelectedMember,
            outcome.ElapsedMilliseconds, outcome.ExpandedNodes, beam?.BestIsVictory, beam?.BestScore,
            beam?.BestRootIndex,
            run.Plan is { } route ? [.. route.Actions.Select(CombatPilotAction.Describe)] : [],
            run.ErrorCode, run.ErrorDetail, encodeError,
            CombatSearchMemberLog.From(outcome.Members, run.MemberRuns));
    }
}

internal sealed record CombatDeviationLog(int SchemaVersion, int Index, string Kind, int Turn,
    int? ActionIndex, int AfterSearch, string[] Diff);
