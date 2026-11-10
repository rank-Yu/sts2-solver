// Progress snapshots and control requests. The shapes follow CombatSolver
// src/Runtime/SolverProgress.cs and src/Search/CombatBeamSolver.Phases.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors), rewritten over Sts2Solver's
// immutable snapshots and packed Battle.
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

/// <summary>Why a search returned an adopted route.</summary>
internal enum SearchAdoptionKind
{
    /// <summary>The caller asked for a concrete candidate version.</summary>
    Requested,

    /// <summary>The caller asked to stop while a candidate was published.</summary>
    Stopped,
}

/// <summary>Final classification of a coordinator request, mirroring the panel's five end states.</summary>
internal enum SearchCompletionKind
{
    Completed,
    Adopted,
    StoppedWithCandidate,
    Cancelled,
    Failed,
}

/// <summary>The adopted route's provenance and the turn it covers.</summary>
internal sealed record SearchAdoptionInfo(
    int CandidateVersion,
    int ThroughTurn,
    SearchAdoptionKind Kind,
    Outcome Outcome)
{
    /// <summary>True when the chosen route contains only death lines; host-only.</summary>
    internal bool OnlyDeathRoutes { get; init; }
}

/// <summary>Stable target kind of a speculative action.</summary>
internal enum SearchProgressTargetKind : byte
{
    None,
    Player,
    Osty,
    Enemy,
}

/// <summary>An enemy a speculative action kills, frozen by stable identity.</summary>
internal sealed record SearchProgressKill(uint CombatId, string MonsterId, int Ordinal);

/// <summary>
/// One speculative action with the stable ids and numbers the panel needs. Names are resolved on
/// the UI thread; the search thread never reads localization.
/// </summary>
internal sealed record SearchProgressAction(
    PlayerActionKind Kind,
    string? CardId,
    int Upgrade,
    string? PotionId,
    string? CardType,
    SearchProgressTargetKind TargetKind,
    uint? TargetCombatId,
    string? TargetMonsterId,
    int TargetOrdinal,
    SearchProgressKill[] Kills,
    int HpBefore,
    int HpAfter,
    int EnergyBefore,
    int EnergyAfter,
    int ReplayCount);

/// <summary>One replay turn of a speculative route.</summary>
internal sealed record SearchProgressTurn(
    int Turn,
    SearchProgressAction[] Actions,
    int HpLost,
    int HpRecovered,
    int EnemyHpLost,
    int EnergyLeft,
    bool CombatEnded);

/// <summary>
/// The retained route the search is currently considering. It is not yet a validated final result;
/// materialization replays it from the request root before it may be adopted.
/// </summary>
internal sealed record SearchRoutePreview(
    int CandidateVersion,
    int StartTurn,
    int ThroughTurn,
    int RootIndex,
    PlayerAction[] Actions,
    SearchProgressTurn[] Turns,
    bool Victory,
    bool OnlyDeathRoutes);

/// <summary>The speculative route's current-turn slice and its end-of-turn values.</summary>
internal sealed record SearchCurrentTurnPreview(
    int CandidateVersion,
    int Turn,
    SearchProgressAction[] Actions,
    int HpLost,
    int HpRecovered,
    int EnemyHpLost,
    int EnergyLeft,
    bool CombatEnded);

/// <summary>The best complete victory a previous member already adopted.</summary>
internal sealed record SearchInterimResult(
    int MemberIndex,
    bool Victory,
    int HpLost,
    int PotionUses,
    int ActionCount,
    double Score,
    int CombatEndedTurn);

/// <summary>
/// Immutable snapshot published by the search thread. The Mod polls it from the main thread; it
/// never carries a live <see cref="Battle"/> or a mutable search object.
/// </summary>
internal sealed record SearchProgressSnapshot(
    int MemberIndex,
    SearchMember Member,
    long ExpandedNodes,
    long ElapsedMilliseconds,
    SearchRoutePreview? Route,
    SearchCurrentTurnPreview? CurrentTurn,
    SearchInterimResult? BestCompleteResult,
    int CandidateVersion)
{
    /// <summary>
    /// Nodes this request expanded so far: the finished members plus the running one.
    /// <see cref="ExpandedNodes"/> is the running member's own count and restarts with every member.
    /// </summary>
    internal long RequestExpandedNodes { get; init; }

    /// <summary>Search-ledger bytes in use when the snapshot was published.</summary>
    internal long SearchMemoryUsedBytes { get; init; }

    /// <summary>The running member's search-ledger peak when the snapshot was published.</summary>
    internal long SearchMemoryPeakBytes { get; init; }
}

/// <summary>A retained candidate with the final state captured at publication time.</summary>
internal sealed record SearchRouteCandidate(
    int CandidateVersion,
    int StartTurn,
    int ThroughTurn,
    int RootIndex,
    PlayerAction[] Actions,
    Battle FinalState,
    SearchProgressTurn[] Turns,
    double Score,
    bool Victory,
    bool OnlyDeathRoutes);

/// <summary>One pending adoption/stop request; only the channel's lock protects it.</summary>
internal readonly record struct SearchControlRequest(int CandidateVersion, bool Stop);

/// <summary>
/// Search-thread progress publication and cross-thread control requests. The search thread is the
/// only writer of snapshots and candidates; the main thread only reads the latest snapshot and
/// posts one request. A null channel means no subscriber and costs the search a single branch at
/// layer boundaries.
/// </summary>
internal sealed class SearchProgressChannel
{
    private const int DefaultRetainedCandidates = 8;

    private readonly object _gate = new();
    private readonly SearchRouteCandidate[] _candidates;
    private int _nextCandidate;
    private int _candidateCount;
    private int _latestCandidateVersion;
    private SearchControlRequest? _request;
    private SearchMember _member = new(1, SearchEvaluation.Standard,
        SearchRetention.Standard, SearchPotionLimit.None);
    private int _memberIndex;
    private SearchInterimResult? _bestComplete;
    private SearchProgressSnapshot? _latest;
    private long _completedExpandedNodes;

    internal SearchProgressChannel(int throttleMilliseconds, int retainedCandidates = DefaultRetainedCandidates)
    {
        ThrottleMilliseconds = Math.Max(0, throttleMilliseconds);
        _candidates = new SearchRouteCandidate[Math.Clamp(retainedCandidates, 1,
            DefaultRetainedCandidates * 4)];
    }

    /// <summary>Minimum wall-clock distance between snapshots; zero publishes every checkpoint.</summary>
    internal int ThrottleMilliseconds { get; }

    /// <summary>Optional publisher-side observer, called with the snapshot after it is stored.</summary>
    internal Action<SearchProgressSnapshot>? Published { get; set; }

    /// <summary>The most recent snapshot, safe to read from any thread.</summary>
    internal SearchProgressSnapshot? Latest => Volatile.Read(ref _latest);

    /// <summary>Filled by the coordinator before the member starts; read only by its search thread.</summary>
    internal void BeginMember(int index, SearchMember member, SearchInterimResult? bestComplete)
    {
        _memberIndex = index;
        _member = member;
        _bestComplete = bestComplete;
    }

    internal int MemberIndex => _memberIndex;
    internal SearchMember Member => _member;
    internal SearchInterimResult? BestComplete => _bestComplete;

    /// <summary>Nodes expanded by the request's finished members; added by the coordinator.</summary>
    internal long CompletedExpandedNodes => Volatile.Read(ref _completedExpandedNodes);

    internal void AddCompletedExpandedNodes(long nodes)
    {
        if (nodes > 0)
            Interlocked.Add(ref _completedExpandedNodes, nodes);
    }


    /// <summary>Stores one snapshot and invokes the publisher-side observer, outside the lock.</summary>
    internal void Publish(SearchProgressSnapshot snapshot)
    {
        Volatile.Write(ref _latest, snapshot);
        Published?.Invoke(snapshot);
    }

    /// <summary>Adds one materializable candidate, evicting the oldest beyond the retained window.</summary>
    internal void AddCandidate(SearchRouteCandidate candidate)
    {
        lock (_gate)
        {
            _candidates[_nextCandidate] = candidate;
            _nextCandidate = (_nextCandidate + 1) % _candidates.Length;
            if (_candidateCount < _candidates.Length)
                _candidateCount++;
            if (candidate.CandidateVersion > _latestCandidateVersion)
                _latestCandidateVersion = candidate.CandidateVersion;
        }
    }

    internal int LatestCandidateVersion
    {
        get { lock (_gate) return _latestCandidateVersion; }
    }

    internal SearchRouteCandidate? LatestCandidate
    {
        get
        {
            lock (_gate)
                return _latestCandidateVersion == 0
                    ? null
                    : FindLocked(_latestCandidateVersion);
        }
    }

    internal SearchRouteCandidate? FindCandidate(int candidateVersion)
    {
        lock (_gate)
            return FindLocked(candidateVersion);
    }

    private SearchRouteCandidate? FindLocked(int candidateVersion)
    {
        for (int offset = 0; offset < _candidateCount; offset++)
        {
            int index = (_nextCandidate - 1 - offset + _candidates.Length) % _candidates.Length;
            SearchRouteCandidate candidate = _candidates[index];
            if (candidate.CandidateVersion == candidateVersion)
                return candidate;
        }
        return null;
    }

    /// <summary>Accepts one adoption request; returns false when another one is already pending.</summary>
    internal bool RequestAdopt(int candidateVersion)
    {
        if (candidateVersion <= 0)
            return false;
        lock (_gate)
        {
            if (_request != null)
                return false;
            _request = new SearchControlRequest(candidateVersion, Stop: false);
            return true;
        }
    }

    /// <summary>Accepts one stop request; returns false when another one is already pending.</summary>
    internal bool RequestStop()
    {
        lock (_gate)
        {
            if (_request != null)
                return false;
            _request = new SearchControlRequest(0, Stop: true);
            return true;
        }
    }

    /// <summary>The search thread consumes the single pending request at a progress checkpoint.</summary>
    internal SearchControlRequest? TryTakeRequest()
    {
        lock (_gate)
        {
            SearchControlRequest? request = _request;
            _request = null;
            return request;
        }
    }
}
