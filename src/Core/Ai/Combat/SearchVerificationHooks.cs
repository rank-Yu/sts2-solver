namespace Sts2Solver.Ai.Combat;

/// <summary>
/// Equivalence-work diagnostics of one solve. None of these values enters the result encoding,
/// the state, or the scoring; they exist so the A/B harness and the verification switch can report
/// how much the two deduplications skipped and how many skipped alternatives were checked.
/// </summary>
internal sealed record SearchEquivalenceDiagnostics(
    long PlayDeduped,
    long PlayVerifiedExact,
    long PlayVerifiedApprox,
    long PlayVerifiedApproxEqual,
    long PlayVerifiedUnresolved,
    long ChoiceByClassRequests,
    long ChoiceVerifiedExact,
    long ChoiceVerifiedApprox,
    long ChoiceVerifiedApproxEqual,
    long ChoiceVerifiedUnresolved)
{
    internal static readonly SearchEquivalenceDiagnostics Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    internal SearchEquivalenceDiagnostics Add(in SearchEquivalenceDiagnostics other)
        => new(PlayDeduped + other.PlayDeduped,
            PlayVerifiedExact + other.PlayVerifiedExact,
            PlayVerifiedApprox + other.PlayVerifiedApprox,
            PlayVerifiedApproxEqual + other.PlayVerifiedApproxEqual,
            PlayVerifiedUnresolved + other.PlayVerifiedUnresolved,
            ChoiceByClassRequests + other.ChoiceByClassRequests,
            ChoiceVerifiedExact + other.ChoiceVerifiedExact,
            ChoiceVerifiedApprox + other.ChoiceVerifiedApprox,
            ChoiceVerifiedApproxEqual + other.ChoiceVerifiedApproxEqual,
            ChoiceVerifiedUnresolved + other.ChoiceVerifiedUnresolved);
}

/// <summary>
/// Default-off verification switches and default-on A/B switches for search optimizations that share
/// work between executions or prune equivalent options. They are test hooks outside
/// <see cref="BeamOptions"/> so a request format never carries a diagnostic or experimental mode.
/// The verification switch only adds comparison passes and never changes the search; the two A/B
/// switches are production defaults that the report harness can turn off one at a time.
/// </summary>
internal static class SearchVerificationHooks
{
    /// <summary>
    /// Product default: deduplicate a play action when an earlier hand card has the same content and
    /// target and every hand position between the two copies is content-equal. Playing either copy
    /// then leaves the same hand sequence, so the skipped child is byte-identical to the
    /// representative's and the search result cannot change. The representative is the lowest hand
    /// index. The harness clears it for the A/B baseline.
    /// </summary>
    internal static bool DedupeExactPlayActions { get; set; } = true;

    /// <summary>
    /// Product default: enumerate a choice request by content classes instead of option indexes, so
    /// the branch cap is spent on distinct content selections. The harness clears it for the A/B
    /// baseline.
    /// </summary>
    internal static bool DedupeEquivalentChoicesByClass { get; set; } = true;

    /// <summary>
    /// Product default: when <see cref="DedupeEquivalentChoicesByClass"/> is on, fill the branch cap
    /// with physical position variants after the class-vector representatives. For a request with at
    /// most 32 selections the original index order is used instead, so only truncated requests see
    /// the class-first order. The M3b follow-up selected this mode: it removes every change in
    /// untruncated requests and keeps a 29-better/13-worse result on the truncated subset.
    /// </summary>
    internal static bool ChoiceFillPhysicalVariants { get; set; } = true;

    /// <summary>
    /// When set before a solve:
    /// <list type="bullet">
    /// <item>every end-turn selection leaf that reuses a prepared prefix also runs the complete
    /// <see cref="Engine.Kernel.Step"/> from its parent and compares the battle bytes, step result,
    /// choice request and history-risk flag;</item>
    /// <item>every threat projection compares the <see cref="Engine.Battle"/> before and after the
    /// in-place projection and reports the first differing field;</item>
    /// <item>every play action skipped by content deduplication, and every physical choice
    /// alternative represented by a class vector, runs from the parent with the same deterministic
    /// choice policy. A selection classified exact by <see cref="ChoiceEnumerator.IsExactByClass"/>
    /// (or by the hand run for plays) must produce byte-identical battles; an approximate one only
    /// contributes to the equality count.</item>
    /// </list>
    /// A difference in an exact check throws immediately.
    /// </summary>
    internal static bool VerifySearchEquivalence { get; set; }

    /// <summary>
    /// Default-off contract switch. The first layer-boundary selection batch runs a dedicated
    /// failing batch first: one worker throws <see cref="SelectionBatchFailure"/>, the search thread
    /// must receive the same exception object from <c>RunBatch</c>, a following batch on the same
    /// worker pool must complete, and the solve must continue through later layers to the same
    /// encoded result. The contract asserts all of this; production never sets the switch.
    /// </summary>
    internal static bool VerifySelectionBatchFailureRecovery { get; set; }

    /// <summary>The exception object thrown by the contract's worker batch.</summary>
    internal static Exception? SelectionBatchFailure { get; set; }

    /// <summary>Set by the contract after <c>RunBatch</c> rethrows the worker exception.</summary>
    internal static Exception? SelectionBatchFailureObserved { get; set; }

    /// <summary>Items completed by the batch run immediately after the failed batch.</summary>
    internal static int SelectionBatchRecoveryItems { get; set; }
}
