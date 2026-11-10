// Ported from CombatSolver src/Search/PotionStrategySnapshot.cs at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members Sts2Solver reads.
namespace Sts2Solver.Ai.Combat.Policy;

internal enum PotionDirective
{
    Smart = 0,
    // The SREQ codec stores the numeric value.
    Disabled = 2,
}

internal readonly record struct PotionSlotDirective(
    int Slot,
    string PotionId,
    PotionDirective Directive);

internal sealed class PotionStrategySnapshot
{
    private readonly Dictionary<(int Slot, string PotionId), PotionDirective> _directives;

    public PotionStrategySnapshot(
        PotionUseMode defaultPolicy,
        IEnumerable<PotionSlotDirective> directives)
    {
        DefaultPolicy = defaultPolicy;
        _directives = directives.ToDictionary(
            directive => (directive.Slot, directive.PotionId),
            directive => directive.Directive);
        Directives = _directives
            .Select(item => new PotionSlotDirective(item.Key.Slot, item.Key.PotionId, item.Value))
            .OrderBy(item => item.Slot)
            .ToArray();
    }

    internal PotionStrategySnapshot Copy()
        => new(DefaultPolicy, Directives);

    public PotionUseMode DefaultPolicy { get; }
    public IReadOnlyList<PotionSlotDirective> Directives { get; }

    public bool AllowsExplicitUse(
        int slot,
        string potionId,
        PotionUseMode effectivePolicy,
        bool forceAllDisabled)
    {
        if (forceAllDisabled)
            return false;
        return _directives.TryGetValue((slot, potionId), out PotionDirective directive)
            ? directive != PotionDirective.Disabled
            : effectivePolicy != PotionUseMode.Disabled;
    }
}
