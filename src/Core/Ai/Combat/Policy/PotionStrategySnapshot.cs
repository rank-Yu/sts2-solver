// Ported from CombatSolver src/Search/PotionStrategySnapshot.cs at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members Sts2Solver reads.
namespace Sts2Solver.Ai.Combat.Policy;

internal enum PotionDirective
{
    Smart,
    Force,
    Disabled,
}

internal readonly record struct PotionSlotDirective(
    int Slot,
    string PotionId,
    PotionDirective Directive);

/// <summary>One explicit potion use with the strategic cost the route paid for it.</summary>
internal readonly record struct PotionUse(
    int Slot,
    string PotionId,
    int StrategicHpCost);

internal readonly record struct ForcedPotionUseEvaluation(
    bool AllForcedUsesSatisfied,
    int ForcedUseCount,
    int ForcedStrategicHpCost,
    int ForcedAmbergrisCount);

internal sealed class PotionStrategySnapshot
{
    private readonly Dictionary<(int Slot, string PotionId), PotionDirective> _directives;
    private readonly bool _onlyForcedUses;

    public PotionStrategySnapshot(
        PotionUseMode defaultPolicy,
        IEnumerable<PotionSlotDirective> directives)
        : this(defaultPolicy, directives, onlyForcedUses: false) { }

    private PotionStrategySnapshot(
        PotionUseMode defaultPolicy,
        IEnumerable<PotionSlotDirective> directives,
        bool onlyForcedUses)
    {
        DefaultPolicy = defaultPolicy;
        _onlyForcedUses = onlyForcedUses;
        _directives = directives.ToDictionary(
            directive => (directive.Slot, directive.PotionId),
            directive => directive.Directive);
        Directives = _directives
            .Select(item => new PotionSlotDirective(item.Key.Slot, item.Key.PotionId, item.Value))
            .OrderBy(item => item.Slot)
            .ToArray();
        foreach (PotionSlotDirective directive in Directives)
            if (directive.Directive == PotionDirective.Force)
                ForcedDirectiveCount++;
        HasForcedDirectives = ForcedDirectiveCount > 0;
    }

    internal PotionStrategySnapshot Copy()
        => new(DefaultPolicy, Directives, _onlyForcedUses);

    public PotionUseMode DefaultPolicy { get; }
    public IReadOnlyList<PotionSlotDirective> Directives { get; }

    public bool HasForcedDirectives { get; }

    public int ForcedDirectiveCount { get; }

    public PotionStrategySnapshot ForForcedBaseline()
        => new(DefaultPolicy, Directives, onlyForcedUses: true);

    public bool AllowsExplicitUse(
        int slot,
        string potionId,
        PotionUseMode effectivePolicy,
        bool forceAllDisabled)
    {
        if (forceAllDisabled)
            return false;
        if (_onlyForcedUses)
            return _directives.TryGetValue((slot, potionId), out PotionDirective forced)
                && forced == PotionDirective.Force;
        return _directives.TryGetValue((slot, potionId), out PotionDirective directive)
            ? directive != PotionDirective.Disabled
            : effectivePolicy != PotionUseMode.Disabled;
    }

    /// <summary>
    /// The existing search's forced-use evaluation over a route's explicit potion uses. A forced
    /// directive is satisfied by the last use with the same slot and identity, matching the original
    /// <c>PredictedPotionUse</c> overload.
    /// </summary>
    public ForcedPotionUseEvaluation EvaluateForcedUses(IReadOnlyList<PotionUse> potionUses)
    {
        int count = 0;
        int strategicCost = 0;
        int ambergrisCount = 0;
        foreach (PotionSlotDirective directive in Directives)
        {
            if (directive.Directive != PotionDirective.Force)
                continue;
            PotionUse? use = null;
            for (int index = potionUses.Count - 1; index >= 0; index--)
            {
                PotionUse candidate = potionUses[index];
                if (candidate.Slot != directive.Slot
                    || !string.Equals(candidate.PotionId, directive.PotionId,
                        StringComparison.Ordinal))
                    continue;
                use = candidate;
                break;
            }
            if (use is not { } matched)
                continue;
            count++;
            strategicCost += matched.StrategicHpCost;
            if (directive.PotionId == "AMBERGRIS")
                ambergrisCount++;
        }
        return new ForcedPotionUseEvaluation(count == ForcedDirectiveCount,
            count, strategicCost, ambergrisCount);
    }

    public string DescribeForcedUses()
        => string.Join(", ", Directives
            .Where(directive => directive.Directive == PotionDirective.Force)
            .Select(directive => $"{directive.PotionId}@{directive.Slot}"));
}
