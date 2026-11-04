using System.Reflection;
using Sts2Sim.Core.Models;
using Sts2Solver.Simulation.Combat;

namespace Sts2SimExport;

internal static class RelicStateExport
{
    internal static readonly IReadOnlyDictionary<string, string[]> StartingRelics =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["IRONCLAD"] = ["BURNING_BLOOD"],
            ["SILENT"] = ["RING_OF_THE_SNAKE"],
            ["DEFECT"] = ["CRACKED_CORE"],
            ["REGENT"] = ["DIVINE_RIGHT"],
            ["NECROBINDER"] = ["BOUND_PHYLACTERY"],
        };

    internal static bool TryExport(RelicModel relic, out ExportRelic exported, out string reason)
    {
        exported = new ExportRelic { RelicId = relic.Id.Entry };
        reason = string.Empty;
        string id = relic.Id.Entry;
        if (!Registry.TryRelic(id, out _))
        {
            reason = "relic-unsupported:" + id;
            return false;
        }

        Dictionary<string, int> ints = new(StringComparer.Ordinal);
        Dictionary<string, bool> bools = new(StringComparer.Ordinal);
        try
        {
            AddCommonState(relic, ints, bools);
            AddRelicSpecificState(relic, ints, bools);
        }
        catch (InvalidOperationException error)
        {
            reason = "relic-state-missing:" + error.Message;
            return false;
        }
        catch (OverflowException error)
        {
            reason = "relic-state-overflow:" + error.Message;
            return false;
        }
        catch (NotSupportedException error)
        {
            reason = "relic-state-unrepresentable:" + error.Message;
            return false;
        }

        exported = new ExportRelic
        {
            RelicId = id,
            AddWithoutObtainedEffects = true,
            IntegerMembers = ints.Count == 0 ? null : ints,
            BooleanMembers = bools.Count == 0 ? null : bools,
        };
        return true;
    }

    private static void AddCommonState(RelicModel relic, Dictionary<string, int> ints,
        Dictionary<string, bool> bools)
    {
        if (relic.StackCount != 1)
            ints["<StackCount>k__BackingField"] = relic.StackCount;
        if (relic.IsWax)
            bools["IsWax"] = true;
        if (relic.IsMelted)
            bools["IsMelted"] = true;
    }

    private static void AddRelicSpecificState(RelicModel relic, Dictionary<string, int> ints,
        Dictionary<string, bool> bools)
    {
        switch (relic.Id.Entry)
        {
            case "NUNCHAKU":
                ints["AttacksPlayed"] = Int(relic, "_attacksPlayed");
                break;
            case "PEN_NIB":
                ints["AttacksPlayed"] = Int(relic, "_attacksPlayed");
                break;
            case "TUNING_FORK":
                ints["SkillsPlayed"] = Int(relic, "_skillsPlayed");
                break;
            case "JOSS_PAPER":
                ints["CardsExhausted"] = Int(relic, "_cardsExhausted");
                ints["_etherealCount"] = Int(relic, "_etherealCount");
                break;
            case "LIZARD_TAIL":
                bools["WasUsed"] = Bool(relic, "_wasUsed");
                break;
            case "THROWING_AXE":
                bools["_usedThisCombat"] = Bool(relic, "_used");
                break;
            case "HAPPY_FLOWER":
                ints["TurnsSeen"] = Int(relic, "_turnCounter");
                break;
            case "VENERABLE_TEA_SET":
            case "FAKE_VENERABLE_TEA_SET":
                bools["GainEnergyInNextCombat"] = Bool(relic, "_isArmed");
                break;
            case "POCKETWATCH":
                ints["_cardsPlayedThisTurn"] = Int(relic, "CardsPlayedThisTurn");
                ints["_cardsPlayedLastTurn"] = Int(relic, "CardsPlayedPreviousTurn");
                break;
            case "RAINBOW_RING":
                ints["_activationCountThisTurn"] = Bool(relic, "_triggeredThisTurn") ? 1 : 0;
                ints["_powersPlayedThisTurn"] = Bool(relic, "_playedPower") ? 1 : 0;
                break;
            case "PAELS_EYE":
                bools["_usedThisCombat"] = Bool(relic, "_usedThisCombat");
                bools["_wasOwnerPartOfLastPlayerTurn"] = Bool(relic, "_wasOwnerPartOfLastPlayerTurn");
                break;
            case "PAELS_TEARS":
                bools["_hadLeftoverEnergy"] = Bool(relic, "_gainNextTurn");
                break;
            case "BEATING_REMNANT":
                if (Int(relic, "_hpLostThisTurn") != 0)
                    throw new NotSupportedException("BEATING_REMNANT._hpLostThisTurn 是非零 Decimal");
                break;
            case "KUNAI":
            case "KUSARIGAMA":
            case "ORNAMENTAL_FAN":
            case "SHURIKEN":
                ints["_attacksPlayedThisTurn"] = Int(relic, "_attacksThisTurn");
                break;
            case "LETTER_OPENER":
                ints["_skillsPlayedThisTurn"] = Int(relic, "_skillsThisTurn");
                break;
            case "METRONOME":
                ints["_orbsChanneled"] = Int(relic, "_orbsChanneled");
                break;
            case "REGALITE":
                bools["_usedThisTurn"] = Bool(relic, "_usedThisTurn");
                break;
            case "VAMBRACE":
                bools["_blockGainedThisCombat"] = Bool(relic, "_triggeredThisCombat");
                break;
            case "BURNING_STICKS":
                bools["_wasUsedThisCombat"] = Bool(relic, "_wasUsedThisCombat");
                break;
            case "CENTENNIAL_PUZZLE":
                bools["UsedThisCombat"] = Bool(relic, "_triggeredThisCombat");
                break;
            case "LAVA_LAMP":
                bools["TookDamageThisCombat"] = Bool(relic, "_tookDamageThisCombat");
                break;
            case "DEMON_TONGUE":
                bools["_triggeredThisTurn"] = Bool(relic, "_triggeredThisTurn");
                break;
            case "VELVET_CHOKER":
                ints["_cardsPlayedThisTurn"] = Int(relic, "_cardsPlayedThisTurn");
                break;
            case "IRON_CLUB":
                ints["CardsPlayed"] = Int(relic, "_cardsPlayed");
                break;
            case "GALACTIC_DUST":
                ints["StarsSpent"] = Int(relic, "_starsSpent");
                break;
            case "MINI_REGENT":
                bools["_usedThisTurn"] = Bool(relic, "_triggeredThisTurn");
                break;
            case "RED_SKULL":
                bools["_strengthApplied"] = Bool(relic, "_strengthApplied");
                break;
            case "FAKE_HAPPY_FLOWER":
                ints["TurnsSeen"] = Int(relic, "_turnsSeen");
                break;
            case "MUSIC_BOX":
                bools["_wasUsedThisTurn"] = Bool(relic, "_wasUsedThisTurn");
                break;
            case "PAELS_LEGION":
                ints["_cooldown"] = Int(relic, "_cooldown");
                bools["_triggeredBlockLastTurn"] = Bool(relic, "_triggeredBlockLastTurn");
                break;
            case "PENDULUM":
                ints["TurnsSeen"] = Int(relic, "_turnCounter");
                break;
            case "PERMAFROST":
                bools["_activatedThisCombat"] = Bool(relic, "_triggeredThisCombat");
                break;
            case "POLLINOUS_CORE":
                ints["TurnsSeen"] = Int(relic, "_handsSeen");
                break;
            case "BONE_TEA":
                ints["CombatsLeft"] = Int(relic, "_combatsLeft");
                break;
            case "EMOTION_CHIP":
                // LostHpInPreviousTurn is derived from combat damage history at the root.
                break;
            case "ART_OF_WAR":
                // Combat start has no previous player turn. The simulator's
                // _previousTurnHadNoAttack is also false before any turn and after a turn with
                // attacks, so it cannot be projected; leaving both flags at their native false
                // defaults is the only faithful pre-combat state.
                break;
            case "BELT_BUCKLE":
                bools["_dexterityApplied"] = Bool(relic, "_dexterityApplied");
                break;
        }
    }

    private static bool Bool(object target, string member) =>
        (bool)Read(target, member);

    private static int Int(object target, string member) =>
        (int)Read(target, member);

    private static object Read(object target, string member)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (Type? type = target.GetType(); type != null; type = type.BaseType)
        {
            PropertyInfo? property = type.GetProperty(member, flags);
            if (property != null)
                return property.GetValue(target)
                    ?? throw new InvalidOperationException(
                        $"{target.GetType().Name}.{member} 为 null");
            FieldInfo? field = type.GetField(member, flags);
            if (field != null)
                return field.GetValue(target)
                    ?? throw new InvalidOperationException(
                        $"{target.GetType().Name}.{member} 为 null");
        }
        throw new InvalidOperationException($"{target.GetType().Name}.{member} 不存在");
    }
}
