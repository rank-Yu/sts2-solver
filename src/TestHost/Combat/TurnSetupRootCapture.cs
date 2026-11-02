// Adapted from CombatSolver/tools/search/OfflineSearchHarness/TurnSetupRootCapture.cs and
// src/Runtime/PlayerTurnSetupPatches.cs at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors).
// Captures the player CombatState at the CombatManager.SetupPlayerTurn entry point, before the
// native setup page runs. The exact card-object set is always kept; the optional Start Battle is
// imported here while the native state is still at PlayerTurnPhase.Start, because the native
// setup mutates the state before the caller resumes.
using HarmonyLib;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Solver.TestHost;

internal static class TurnSetupRootCapture
{
    private static Harmony? _harmony;

    public static bool Enabled { get; set; }

    /// <summary>
    /// Import the Start Battle (with rules) in the prefix. Only the differential setup enumerator
    /// sets this; the ordinary Play-root capture and the live comparison do not.
    /// </summary>
    public static bool CaptureStartBattle { get; set; }

    /// <summary>Card objects present in the player's combat piles when SetupPlayerTurn was entered.</summary>
    public static HashSet<CardModel>? StartCards { get; private set; }

    /// <summary>The Start root imported in the prefix when <see cref="CaptureStartBattle"/> is set.</summary>
    public static Battle? StartBattle { get; private set; }

    /// <summary>The rules compiled for <see cref="StartBattle"/>.</summary>
    public static Rules? StartRules { get; private set; }

    /// <summary>
    /// The same Start root imported with the production reconciliation semantics
    /// (<see cref="LiveCombatTracker.ImportSetupRoot"/>).
    /// </summary>
    public static Battle? ProductionStartBattle { get; private set; }

    /// <summary>Tracker created at the Start snapshot, reused for the plan Play-root comparison.</summary>
    public static LiveCombatTracker? StartTracker { get; private set; }

    /// <summary><c>CombatTurnState.PlayersTakingExtraTurn.Count &gt; 0</c> at the Start snapshot.</summary>
    public static bool StartTakingExtraTurn { get; private set; }

    /// <summary>The native player turn number at the Start snapshot.</summary>
    public static int StartTurn { get; private set; }

    public static void Install()
    {
        if (_harmony != null)
            return;
        _harmony = new Harmony("Sts2Solver.TurnSetupRootCapture");
        _harmony.Patch(
            AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn"),
            prefix: new HarmonyMethod(typeof(TurnSetupRootCapture), nameof(Prefix)));
    }

    public static void BeginBuild(bool captureStartBattle = false)
    {
        StartCards = null;
        StartBattle = null;
        ProductionStartBattle = null;
        StartTracker = null;
        StartRules = null;
        StartTakingExtraTurn = false;
        StartTurn = 0;
        CaptureStartBattle = captureStartBattle;
        Enabled = true;
    }

    internal static bool Prefix(CombatManager __instance, CombatTurnState turnState)
    {
        if (!Enabled || StartCards != null)
            return true;
        CombatState? combat = __instance.DebugOnlyGetState();
        if (combat == null)
            return true;
        Player? player = LocalContext.GetMe(combat);
        if (player?.PlayerCombatState is not { Phase: PlayerTurnPhase.Start })
            return true;
        HashSet<CardModel> cards = new(ReferenceEqualityComparer.Instance);
        AddCards(cards, player.PlayerCombatState.Hand);
        AddCards(cards, player.PlayerCombatState.DrawPile);
        AddCards(cards, player.PlayerCombatState.DiscardPile);
        AddCards(cards, player.PlayerCombatState.ExhaustPile);
        AddCards(cards, player.PlayerCombatState.PlayPile);
        StartCards = cards;
        StartTurn = player.PlayerCombatState.TurnNumber;
        StartTakingExtraTurn = turnState.PlayersTakingExtraTurn.Count > 0;
        if (CaptureStartBattle)
        {
            if (combat.RunState is not RunState runState)
                throw new InvalidDataException("回合准备 Start 快照的 RunState 不是具体 RunState。");
            Rules rules = RulesCompiler.Compile(combat, player, runState);
            StartRules = rules;
            StartBattle = BattleImporter.Import(combat, player, rules,
                ImportOptions.Default with
                {
                    RequirePlayPhase = false,
                    TurnSetupRoot = true,
                    RootCards = cards,
                });
            LiveCombatTracker tracker = new(combat);
            StartTracker = tracker;
            ProductionStartBattle = tracker.ImportSetupRoot(combat, player, rules);
        }
        return true;
    }

    private static void AddCards(HashSet<CardModel> cards, CardPile pile)
    {
        foreach (CardModel card in pile.Cards)
            cards.Add(card);
    }
}
