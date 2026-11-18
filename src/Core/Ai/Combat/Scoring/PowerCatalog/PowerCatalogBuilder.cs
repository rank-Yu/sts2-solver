// Adapted from CombatSolver src/Flash/Bridge/FlashPowerCommitmentBridge.cs at a62f1850
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat.Scoring;

internal static class PowerCatalogBuilder
{
    internal static PowerCommitmentCatalog? Build(in Battle root, Rules rules, Scorer scorer, int floor)
    {
        bool hasRegisteredRoot = false;
        for (int slot = 0; slot < root.CardCount; slot++)
        {
            Card card = root.Cards[slot];
            if (Lookup(Registry.Card(card.Id).NativeId).Item1 != PowerCommitmentFamily.None)
            {
                hasRegisteredRoot = true;
                break;
            }
        }
        if (!hasRegisteredRoot) return null;
        PowerCommitmentRule[] entries = new PowerCommitmentRule[scorer.RulesForCodec.Cards.Length];
        int index = 0;
        for (ushort cardId = 0; cardId < entries.Length; cardId++)
        {
            (PowerCommitmentFamily family, PowerRouteAdmissionPolicy policy) =
                Lookup(Registry.Card(cardId).NativeId);
            if (family == PowerCommitmentFamily.None) continue;
            if (index >= 128)
                throw new CapacityException("PowerCommitment.Cards", 128);
            entries[cardId] = new(family, policy.Priority, policy, index++);
        }
        return index == 0 ? null : new PowerCommitmentCatalog(entries, Math.Max(0, floor));
    }

    private static (PowerCommitmentFamily, PowerRouteAdmissionPolicy) Lookup(string id)
    {
        PowerCommitmentFamily family = IroncladPowerRoutePolicy.FamilyFor(id);
        if (family != PowerCommitmentFamily.None) return (family, IroncladPowerRoutePolicy.For(id));
        family = SilentPowerRoutePolicy.FamilyFor(id);
        if (family != PowerCommitmentFamily.None) return (family, SilentPowerRoutePolicy.For(id));
        family = DefectPowerRoutePolicy.FamilyFor(id);
        if (family != PowerCommitmentFamily.None) return (family, DefectPowerRoutePolicy.For(id));
        family = RegentPowerRoutePolicy.FamilyFor(id);
        if (family != PowerCommitmentFamily.None) return (family, RegentPowerRoutePolicy.For(id));
        family = NecrobinderPowerRoutePolicy.FamilyFor(id);
        if (family != PowerCommitmentFamily.None) return (family, NecrobinderPowerRoutePolicy.For(id));
        family = FoolPowerRoutePolicy.Lookup(id).Family;
        if (family != PowerCommitmentFamily.None)
            return (family, new(PowerRoutePriority.Strong, RequirePositiveProjection: true));
        family = ColorlessPowerRoutePolicy.FamilyFor(id);
        return (family, ColorlessPowerRoutePolicy.For(id));
    }
}
