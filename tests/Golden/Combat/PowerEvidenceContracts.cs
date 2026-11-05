using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

internal static class PowerEvidenceContracts
{
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidDataException("power-evidence: " + message); }

    private static SearchCase Read(string root)
    {
        using FileStream file = File.OpenRead($"tests/Golden/data/d1/quality-{root}/config.br");
        using BrotliStream zip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new(); zip.CopyTo(bytes);
        return SearchCodec.DecodeRequest(bytes.ToArray());
    }

    private static (Battle Parent, Battle Child, ushort CardId) Play(SearchCase item, string id, int? initialAmount = null, ushort powerId = 0, bool moveToHand = false)
    {
        Battle parent = item.Battles[0];
        if (initialAmount is { } amount) Kernel.SetPowerAmount(ref parent, Limits.PlayerOwner, powerId, amount);
        if (moveToHand)
        {
            for (int i = parent.HandCount; i < parent.HandCount + parent.DrawCount + parent.DiscardCount; i++)
            {
                if (Registry.Card(parent.Cards[i].Id).NativeId != id) continue;
                Pile pile = Piles.PileOf(parent, i, out int local);
                Card card = Piles.RemoveAt(ref parent, pile, local);
                Piles.Append(ref parent, Pile.Hand, card);
                break;
            }
        }
        PlayerAction[] actions = new PlayerAction[Kernel.MaxLegalActions];
        int count = Kernel.LegalActions(parent, item.Rules, actions);
        for (int i = 0; i < count; i++)
        {
            PlayerAction action = actions[i];
            if (action.Kind != PlayerActionKind.PlayCard || Registry.Card(parent.Cards[action.HandIndex].Id).NativeId != id) continue;
            ushort cardId = parent.Cards[action.HandIndex].Id;
            Battle child = parent;
            if (Kernel.Step(ref child, action, item.Rules, out _, out _) == StepResult.Completed)
                return (parent, child, cardId);
        }
        throw new InvalidDataException("power-evidence: missing playable " + id);
    }

    private static PowerRouteAdmissionResult Admission(SearchCase item, in Battle parent, in Battle child,
        ushort cardId, scoped in PowerEvidence.View view, in ScoreFeatures before, in ScoreFeatures after)
    {
        string id = Registry.Card(cardId).NativeId;
        PowerCommitmentCatalog catalog = PowerCatalogBuilder.Build(parent, item.Rules, item.Options.Scorer!, 0)
            ?? throw new InvalidDataException("power-evidence: missing catalog");
        int setup = Math.Max(0, after.PersistentBuffValue - before.PersistentBuffValue)
            + Math.Max(0, after.StrategicRetentionValue - before.StrategicRetentionValue)
            + Math.Max(0, after.FutureResourceValue - before.FutureResourceValue)
            + Math.Max(0, after.LatentSetupValue - before.LatentSetupValue);
        return PowerRouteAdmission.Evaluate(new(id, false, Math.Max(0, parent.Energy-child.Energy), child.Energy,
            PowerEvidence.HasTriggerEvidence(cardId, parent, view),
            Math.Max(0, after.ProjectedPlayerHp-before.ProjectedPlayerHp), setup,
            PowerEvidence.ProjectionPotential(cardId, view, parent, before.ProjectedPlayerHp), PowerEvidence.TriggerProjectionFloor(cardId, view), 0),
            catalog.Rules[cardId].Admission);
    }

    internal static void Run()
    {
        CheckActualGains();
        CheckTwoStatAndWraith();
        CheckMasterPlanner();
        CheckSubroutine();
        CheckBuffer();
        CheckSetupRanking();
        SilentPowerProjectionContracts.Run();
        Console.WriteLine("POWER_EVIDENCE actual transitions/identity/admission/threat=pass");
    }

    private static void CheckActualGains()
    {
        foreach ((string root, string card, ushort effect, int expected, int initial) in new[]
        {
            ("DEFECT-011", "DEFRAGMENT", PowerEvidenceIds.Focus, 2, 4),
            ("IRONCLAD-011", "INFLAME", PowerEvidenceIds.Strength, 3, -2),
            ("IRONCLAD-016", "DARK_EMBRACE", PowerEvidenceIds.DarkEmbrace, 1, 2),
        })
        {
            SearchCase item = Read(root); var (parent, child, _) = Play(item, card, initial, effect);
            Scorer scorer = item.Options.Scorer!; ScoreFeatures features = scorer.Features(child, 1);
            PowerEvidence.View view = new(child, features, item.Rules, scorer);
            Assert(view.PowerAmountGain(parent, effect) == expected, card + " actual signed increment");
            Assert(Kernel.GetAmount(child, Limits.PlayerOwner, effect) == initial + expected, card + " actual transition");
            Battle extra = child;
            Kernel.SetPowerAmount(ref extra, Limits.PlayerOwner, PowerEvidenceIds.Dexterity, 19);
            PowerEvidence.View extraView = new(extra, features, item.Rules, scorer);
            Assert(extraView.PowerAmountGain(parent, effect) == expected, card + " unrelated effect ignored");
            PowerEvidence.View unchanged = new(parent, features, item.Rules, scorer);
            Assert(unchanged.PowerAmountGain(parent, effect) == 0, card + " zero increment");
            Battle negative = parent;
            Kernel.SetPowerAmount(ref negative, Limits.PlayerOwner, effect, initial - 1);
            PowerEvidence.View decrease = new(negative, features, item.Rules, scorer);
            Assert(decrease.PowerAmountGain(parent, effect) == 0, card + " negative increment");
        }
        SearchCase probe = Read("DEFECT-011"); Battle state = probe.Battles[0];
        Kernel.SetPowerAmount(ref state, Limits.PlayerOwner, PowerEvidenceIds.Focus, 6);
        Kernel.SetPowerAmount(ref state, Limits.PlayerOwner, PowerEvidenceIds.Strength, 4);
        Kernel.SetPowerAmount(ref state, Limits.PlayerOwner, PowerEvidenceIds.Plating, 3);
        byte enemy = Kernel.EnemyOwner(state.Roster[0]);
        Kernel.SetPowerAmount(ref state, enemy, PowerEvidenceIds.Poison, 7);
        Kernel.SetPowerAmount(ref state, enemy, PowerEvidenceIds.Weak, 2);
        Scorer score = probe.Options.Scorer!; ScoreFeatures f = score.Features(state, 0);
        PowerEvidence.View facts = new(state, f, probe.Rules, score);
        Assert(facts.PlayerPowerAmount(PowerEvidenceIds.Focus) == 6
            && facts.PlayerPowerAmount(PowerEvidenceIds.Strength) == 4
            && facts.PlayerPowerAmount(PowerEvidenceIds.Plating) == 3, "canonical player power IDs");
        Assert(facts.EnemyHasPower(PowerEvidenceIds.Poison) && facts.EnemyHasPower(PowerEvidenceIds.Weak)
            && facts.EnemyPowerAmount(state.Roster[0], PowerEvidenceIds.Poison) == 7, "canonical enemy power IDs");
    }

    private static void CheckTwoStatAndWraith()
    {
        SearchCase item = Read("IRONCLAD-002"); var (parent, child, cardId) = Play(item, "PROWESS");
        Scorer scorer = item.Options.Scorer!; ScoreFeatures features = scorer.Features(child, 1);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        Assert(view.PowerAmountGain(parent, PowerEvidenceIds.Strength) == 2
            && view.PowerAmountGain(parent, PowerEvidenceIds.Dexterity) == 1,
            "Prowess keeps distinct strength and dexterity gains");
        Assert(PowerEvidence.ProjectionPotential(cardId, view, parent, features.ProjectedPlayerHp) > 0
            && PowerEvidence.ProjectionPotential(cardId, view, child, features.ProjectedPlayerHp) == 0,
            "two-stat projection uses incremental effects");
        Battle wraithParent = parent, wraithChild = parent;
        Kernel.SetPowerAmount(ref wraithParent, Limits.PlayerOwner, PowerEvidenceIds.Intangible, 1);
        Kernel.SetPowerAmount(ref wraithChild, Limits.PlayerOwner, PowerEvidenceIds.Intangible, 3);
        ScoreFeatures threatened = features with { ProjectedPlayerHp = 0 };
        PowerEvidence.View wraithView = new(wraithChild, threatened, item.Rules, scorer);
        Assert(wraithView.WraithTiming(wraithParent) && !wraithView.WraithTiming(wraithChild),
            "Wraith timing reads actual intangible delta");
        Assert(Registry.TryCard("SEEKING_EDGE", out ushort seeking), "Seeking Edge registered");
        Assert(PowerEvidence.ProjectionPotential(seeking, view, parent, features.ProjectedPlayerHp) > 0,
            "Seeking Edge uses existing energy-unit projection rather than unrelated status gain");
    }

    private static void CheckMasterPlanner()
    {
        SearchCase item = Read("SILENT-014"); var (parent, child, cardId) = Play(item, "MASTER_PLANNER");
        Scorer scorer = item.Options.Scorer!; ScoreFeatures before = scorer.Features(parent, 0), after = scorer.Features(child, 1);
        PowerEvidence.View view = new(child, after, item.Rules, scorer);
        Assert(PowerEvidence.ProjectionPotential(cardId, view, parent, before.ProjectedPlayerHp) > 0
            && Admission(item, parent, child, cardId, view, before, after).Admitted,
            "Master Planner real playable source has admitted projection");
        Battle noWindows = child;
        Card replacement = default;
        for (int i = 0; i < view.LiveCount; i++)
            if (Registry.Card(child.Cards[i].Id).NativeId == "STRIKE_SILENT") { replacement = child.Cards[i]; break; }
        Assert(Registry.Card(replacement.Id).NativeId == "STRIKE_SILENT", "attack replacement exists");
        for (int i = 0; i < view.LiveCount; i++)
            if (Registry.Card(noWindows.Cards[i].Id).NativeId is "SURVIVOR" or "ACROBATICS" or "DAGGER_THROW" or "PREPARED" or "HIDDEN_DAGGERS" or "STORM_OF_STEEL" or "SHADOW_STEP" or "CALCULATED_GAMBLE")
                noWindows.Cards[i] = replacement;
        Kernel.SetPowerAmount(ref noWindows, Limits.PlayerOwner, PowerEvidenceIds.ToolsOfTheTrade, 0);
        PowerEvidence.View noWindowView = new(noWindows, after, item.Rules, scorer);
        Assert(PowerEvidence.ProjectionPotential(cardId, noWindowView, parent, before.ProjectedPlayerHp) == 0
            && !Admission(item, parent, noWindows, cardId, noWindowView, before, after).Admitted, "no discard window is rejected");
        Battle allSly = child;
        for (int i = 0; i < view.LiveCount; i++)
            if (Kernel.ResolveCardType(allSly, item.Rules, allSly.Cards[i]) == CardType.Skill)
                allSly.Cards[i].Flags |= CardFlags.SlyThisTurn;
        PowerEvidence.View slyView = new(allSly, after, item.Rules, scorer);
        Assert(PowerEvidence.ProjectionPotential(cardId, slyView, parent, before.ProjectedPlayerHp) == 0
            && !Admission(item, parent, allSly, cardId, slyView, before, after).Admitted, "temporary Sly prevents projection");
        Battle expensive = child; expensive.Energy = 0;
        for (int i = 0; i < view.LiveCount; i++)
        {
            expensive.Cards[i].BaseCost = 100;
            expensive.Cards[i].Flags &= ~CardFlags.CostsX;
            expensive.Cards[i].ModifierCount = 0;
        }
        PowerEvidence.View expensiveView = new(expensive, after, item.Rules, scorer);
        Assert(PowerEvidence.ProjectionPotential(cardId, expensiveView, parent, before.ProjectedPlayerHp) == 0
            && !Admission(item, parent, expensive, cardId, expensiveView, before, after).Admitted, "unpayable skills do not project");
        ScoreFeatures shortFight = after with { EnemyHp = 1, RetainedAttackValue = 100, LiveDeckSize = 1 };
        PowerEvidence.View lateView = new(child, shortFight, item.Rules, scorer);
        Assert(PowerEvidence.ProjectionPotential(cardId, lateView, parent, before.ProjectedPlayerHp) == 0, "payoff after estimated fight does not project");
        Battle noDraw = child;
        Assert(Registry.TryPower("MACHINE_LEARNING_POWER", out ushort drawId), "draw adjustment registered");
        Kernel.SetPowerAmount(ref noDraw, Limits.PlayerOwner, drawId, -100);
        PowerEvidence.View noDrawView = new(noDraw, after, item.Rules, scorer);
        Assert(PowerEvidence.ProjectionPotential(cardId, noDrawView, parent, before.ProjectedPlayerHp) == 0, "zero future draw does not project");
        Assert(PowerEvidence.ProjectionPotential(cardId, view, child, after.ProjectedPlayerHp) == 0, "no actual Master Planner increment");
    }

    private static void CheckSubroutine()
    {
        SearchCase item = Read("DEFECT-002"); var (parent, child, cardId) = Play(item, "SUBROUTINE", moveToHand: true);
        Scorer scorer = item.Options.Scorer!; ScoreFeatures before = scorer.Features(parent, 0), after = scorer.Features(child, 1);
        PowerEvidence.View view = new(child, after, item.Rules, scorer);
        Assert(PowerEvidence.ProjectionPotential(cardId, view, parent, after.ProjectedPlayerHp) > 0
            && Admission(item, parent, child, cardId, view, before, after).Admitted, "Subroutine real playable power admitted");
        Battle unavailable = child;
        for (int i = 0; i < view.LiveCount; i++)
            if (Kernel.ResolveCardType(unavailable, item.Rules, unavailable.Cards[i]) == CardType.Power)
            { unavailable.Cards[i].BaseCost = 100; unavailable.Cards[i].Flags &= ~CardFlags.CostsX; unavailable.Cards[i].ModifierCount = 0; }
        PowerEvidence.View blocked = new(unavailable, after, item.Rules, scorer);
        Assert(!PowerEvidence.HasTriggerEvidence(cardId, parent, blocked)
            && PowerEvidence.ProjectionPotential(cardId, blocked, parent, after.ProjectedPlayerHp) == 0
            && !Admission(item, parent, unavailable, cardId, blocked, before, after).Admitted, "unpayable powers do not trigger Subroutine");
        Assert(PowerEvidence.ProjectionPotential(cardId, view, child, after.ProjectedPlayerHp) == 0, "no Subroutine increment");
        Battle noDraw = child;
        Assert(Registry.TryPower("MACHINE_LEARNING_POWER", out ushort drawId), "draw adjustment registered");
        Kernel.SetPowerAmount(ref noDraw, Limits.PlayerOwner, drawId, -100);
        for (int i = 0; i < noDraw.HandCount; i++)
            if (Kernel.ResolveCardType(noDraw, item.Rules, noDraw.Cards[i]) == CardType.Power)
            { noDraw.Cards[i].BaseCost = 100; noDraw.Cards[i].Flags &= ~CardFlags.CostsX; noDraw.Cards[i].ModifierCount = 0; }
        PowerEvidence.View noDrawView = new(noDraw, after, item.Rules, scorer);
        Assert(noDrawView.PlayablePowerSources() == 0 && PowerEvidence.ProjectionPotential(cardId, noDrawView, parent, after.ProjectedPlayerHp) == 0,
            "zero draw excludes future Subroutine sources");
    }

    private static void CheckBuffer()
    {
        Assert(PowerEvidence.BufferPotential(1, 20, 4) == 5 && PowerEvidence.BufferPotential(2, 20, 4) == 10
            && PowerEvidence.BufferPotential(1, 20, 1) == 20 && PowerEvidence.BufferPotential(0, 20, 4) == 0,
            "Buffer single/multihit prevention");
        SearchCase item = Read("DEFECT-022"); var (parent, child, cardId) = Play(item, "BUFFER");
        Scorer scorer = item.Options.Scorer!; ScoreFeatures before = scorer.Features(parent, 0), after = scorer.Features(child, 1);
        PowerEvidence.View view = new(child, after, item.Rules, scorer);
        Assert(before.ProjectedPlayerHp < after.ProjectedPlayerHp && after.ProjectedPlayerHp == after.PlayerHp
            && view.IncomingAttackHits(false) > 0 && PowerEvidence.HasTriggerEvidence(cardId, parent, view)
            && Admission(item, parent, child, cardId, view, before, after).Admitted,
            "Buffer remains eligible when current attack damage is fully prevented");
        Assert(!PowerEvidence.HasTriggerEvidence(cardId, child, view)
            && PowerEvidence.ProjectionPotential(cardId, view, child, after.ProjectedPlayerHp) == 0, "no Buffer increment");
        Battle peaceful = child;
        for (int i = 0; i < peaceful.EnemyCount; i++) peaceful.Enemies[i].Flags |= EnemyFlags.SkipNextMove;
        ScoreFeatures safe = after with { ProjectedPlayerHp = after.PlayerHp };
        PowerEvidence.View quiet = new(peaceful, safe, item.Rules, scorer);
        Assert(!PowerEvidence.HasTriggerEvidence(cardId, parent, quiet)
            && PowerEvidence.ProjectionPotential(cardId, quiet, parent, after.ProjectedPlayerHp) == 0, "no threat has no Buffer evidence");
        SearchCase future = Read("DEFECT-011"); Battle start = future.Battles[0], buffed = start;
        Kernel.SetPowerAmount(ref buffed, Limits.PlayerOwner, PowerEvidenceIds.Buffer, 1);
        Scorer futureScorer = future.Options.Scorer!; ScoreFeatures futureFeatures = futureScorer.Features(buffed, 0);
        futureFeatures = futureFeatures with { ProjectedPlayerHp = futureFeatures.PlayerHp };
        PowerEvidence.View futureView = new(buffed, futureFeatures, future.Rules, futureScorer);
        Assert(futureView.IncomingAttackHits(false) == 0 && futureView.IncomingAttackHits(true) > 0
            && PowerEvidence.HasTriggerEvidence(cardId, start, futureView), "known successor supplies future attack evidence");
        CheckDynamicIntent(futureScorer, futureFeatures);
    }

    private static void CheckDynamicIntent(Scorer scorer, ScoreFeatures features)
    {
        Assert(Registry.TryMonster("TEST_SUBJECT", out ushort monster), "dynamic attack fixture monster");
        RulesDraft draft = new();
        draft.SetMonster(0, new MonsterRules
        {
            MonsterId = monster, InitialNode = 0, StunnedNode = 1, InitialHpMin = 1, InitialHpMax = 1, StaticInts = [],
            Nodes = [new MonsterNode { NativeId = "MULTI_CLAW_MOVE", Kind = MonsterNodeKind.Move, FollowUp = 2,
                          Attacks = [new MonsterAttack(4, 1)] },
                     new MonsterNode { NativeId = "STUNNED", Kind = MonsterNodeKind.Move },
                     new MonsterNode { NativeId = "NEXT", Kind = MonsterNodeKind.Move, Attacks = [new MonsterAttack(7, 2)] }],
        });
        Rules rules = draft.Build(); Battle battle = default; battle.EnemyCount = 1; battle.RosterCount = 1;
        battle.Roster[0] = 0; battle.Enemies[0].Creature.CurrentHp = 20; battle.Enemies[0].MonsterId = monster;
        battle.Enemies[0].Rules = 0; battle.Enemies[0].Move = 0; battle.Enemies[0].Ai[1] = 3;
        ulong hash = Codec.BattleHash(battle);
        PowerEvidence.View view = new(battle, features, rules, scorer);
        Assert(view.IncomingAttackHits(false) == 4 && view.IncomingAttackHits(true) == 2,
            "current dynamic repeats and direct next move");
        RulesDraft uncertainDraft = new();
        uncertainDraft.SetMonster(0, new MonsterRules
        {
            MonsterId = monster, InitialNode = 0, StunnedNode = 1, InitialHpMin = 1, InitialHpMax = 1, StaticInts = [],
            Nodes = [new MonsterNode { NativeId = "MULTI_CLAW_MOVE", Kind = MonsterNodeKind.Move, FollowUp = 2,
                          Attacks = [new MonsterAttack(4, 1)] },
                     new MonsterNode { NativeId = "STUNNED", Kind = MonsterNodeKind.Move },
                     new MonsterNode { NativeId = "RANDOM", Kind = MonsterNodeKind.RandomBranch,
                          Options = [new RandomBranchOption(0, 1, MoveRepeatType.CanRepeatForever, 0, 0)] }],
        });
        PowerEvidence.View unknown = new(battle, features, uncertainDraft.Build(), scorer);
        Assert(unknown.IncomingAttackHits(true) == 0, "random successor is not predicted");
        Assert(Codec.BattleHash(battle) == hash, "threat read does not mutate battle or RNG");
        battle.Enemies[0].Flags |= EnemyFlags.SkipNextMove;
        PowerEvidence.View skipped = new(battle, features, rules, scorer);
        Assert(skipped.IncomingAttackHits(false) == 0 && skipped.IncomingAttackHits(true) == 0,
            "skipped moves do not invent threats");
    }
    private static void CheckSetupRanking()
    {
        SearchCase transientCase = Read("DEFECT-011");
        Battle transientRoot = transientCase.Battles[0], transient = transientRoot;
        Assert(Registry.TryPower("DRAW_CARDS_NEXT_TURN_POWER", out ushort delayedDraw), "draw-next-turn power registered");
        Kernel.SetPowerAmount(ref transient, Limits.PlayerOwner, delayedDraw, 2);
        Scorer activation = new(transientCase.Options.Scorer!.RulesForCodec,
            setup: SetupSearchPolicy.Capture(transientRoot, transientCase.Rules));
        Assert(activation.Features(transient, 0).SetupPotentialHp == 0,
            "one-shot next-turn draw is not a power-card activation");
        foreach (string character in new[] { "IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT" })
        {
            bool foundGain = false;
            for (int n = 0; n < 24 && !foundGain; n++)
            {
                SearchCase item = Read($"{character}-{n:000}");
                Battle root = item.Battles[0];
                Scorer plain = item.Options.Scorer!;
                Scorer setup = new(plain.RulesForCodec, setup: SetupSearchPolicy.Capture(root, item.Rules));
                ScoreFeatures start = setup.Features(root, 0);
                Assert(start.SetupPotentialHp == 0 && start.Score == plain.Features(root, 0).Score,
                    character + " starting buffs do not earn setup reward");
                for (int i = 0; i < root.HandCount && !foundGain; i++)
                {
                    if (item.Rules.Card(root.Cards[i].Id, root.Cards[i].Upgrade).Type != CardType.Power) continue;
                    PlayerAction[] actions = new PlayerAction[Kernel.MaxLegalActions];
                    int count = Kernel.LegalActions(root, item.Rules, actions);
                    for (int a = 0; a < count && !foundGain; a++)
                    {
                        if (actions[a].Kind != PlayerActionKind.PlayCard || actions[a].HandIndex != i) continue;
                        Battle child = root;
                        if (Kernel.Step(ref child, actions[a], item.Rules, out _, out _) != StepResult.Completed
                            || child.Outcome != Outcome.None) continue;
                        ScoreFeatures gain = setup.Features(child, 1);
                        if (gain.SetupPotentialHp <= 0) continue;
                        Assert(gain.Score > plain.Features(child, 1).Score, character + " effective power changes candidate ordering");
                        SearchCase encoded = item with { Options = item.Options with { Scorer = setup } };
                        Scorer restored = SearchCodec.DecodeRequest(SearchCodec.EncodeRequest(encoded)).Options.Scorer!;
                        Assert(restored.Features(child, 1).Score == gain.Score
                            && restored.Features(child, 1).SetupPotentialHp == gain.SetupPotentialHp,
                            character + " frozen baseline survives single-member transport");
                        Battle terminal = child; terminal.Outcome = Outcome.Victory;
                        Assert(setup.Features(terminal, 1).Score == plain.Features(terminal, 1).Score,
                            character + " setup does not alter final score");
                        foundGain = true;
                    }
                }
            }
            Assert(foundGain, character + " has a supported real power transition");
        }
    }

}
