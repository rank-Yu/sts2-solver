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
using NativeCreature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Solver.TestHost;

internal static class TurnSetupRootCapture
{
    private static Harmony? _harmony;
    private static int _earlyEnemyCurrentHp;
    private static int[]? _earlyInitialEnemyCurrentHps;
    private static bool _earlyMaxHpGuard;
    private static List<(NativeCreature Enemy, int MaxHp)>? _earlyOriginalMaxHp;

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

    /// <summary>
    /// Applies the request's enemy HP before the first <c>SetupPlayerTurn</c>, and therefore before
    /// the auto-pre-play phase. Start-of-combat auto-plays (Imbued, Whispering Earring) can otherwise
    /// kill a naturally spawned enemy before the normal Play-root injection runs, ending the combat
    /// while the caller is still waiting for the play phase. <see cref="int.MaxValue"/> is the
    /// exporter's "full HP" sentinel; it is applied uncapped temporarily so the setup cannot kill
    /// the enemy, and the normal injection later resets the current HP to the native maximum.
    /// </summary>
    public static void PrepareEarlyEnemyHp(OfflineRequest request, bool guardAgainstSetupDeaths)
    {
        _earlyInitialEnemyCurrentHps = request.InitialEnemyCurrentHps.Length > 0
            ? [.. request.InitialEnemyCurrentHps]
            : null;
        _earlyEnemyCurrentHp = _earlyInitialEnemyCurrentHps == null && request.EnemyCurrentHp > 1
            ? request.EnemyCurrentHp
            : 0;
        // The exporter writes int.MaxValue when the root has full enemy HP. Start-of-combat
        // auto-plays can deal more damage than the native maximum before the Play root is reached;
        // temporarily inflate the maximum so the combat survives the preamble, then restore it
        // before the normal Play-root injection.
        _earlyMaxHpGuard = guardAgainstSetupDeaths
            && _earlyInitialEnemyCurrentHps == null
            && request.EnemyCurrentHp == int.MaxValue;
    }

    /// <summary>Restores the native maximum HP changed by the temporary setup guard.</summary>
    public static void RestoreEarlyEnemyMaxHp()
    {
        if (_earlyOriginalMaxHp is not { } originals)
            return;
        _earlyOriginalMaxHp = null;
        foreach ((NativeCreature enemy, int maxHp) in originals)
        {
            enemy.SetMaxHpInternal(maxHp);
            enemy.SetCurrentHpInternal(maxHp);
        }
    }

    public static void BeginBuild(bool captureStartBattle = false)
    {
        Reset();
        CaptureStartBattle = captureStartBattle;
        Enabled = true;
    }

    /// <summary>
    /// Drops the static capture state between requests. The tracker's event subscription is
    /// released with the tracker reference; the native state itself is owned by the request.
    /// </summary>
    public static void Reset()
    {
        Enabled = false;
        CaptureStartBattle = false;
        StartCards = null;
        StartBattle = null;
        ProductionStartBattle = null;
        StartTracker = null;
        StartRules = null;
        StartTakingExtraTurn = false;
        StartTurn = 0;
        _earlyEnemyCurrentHp = 0;
        _earlyInitialEnemyCurrentHps = null;
        _earlyMaxHpGuard = false;
        _earlyOriginalMaxHp = null;
    }

    internal static bool Prefix(CombatManager __instance, CombatTurnState turnState)
    {
        ApplyEarlyEnemyHp(__instance);
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

    private static void ApplyEarlyEnemyHp(CombatManager manager)
    {
        if (_earlyEnemyCurrentHp <= 1 && _earlyInitialEnemyCurrentHps == null)
            return;
        CombatState? combat = manager.DebugOnlyGetState();
        if (combat == null)
            return;
        if (_earlyInitialEnemyCurrentHps is { } currentHps)
        {
            if (currentHps.Length != combat.Enemies.Count)
                throw new InvalidDataException(
                    $"逐敌生命数量 {currentHps.Length} 与敌人数 {combat.Enemies.Count} 不同。");
            for (int index = 0; index < currentHps.Length; index++)
            {
                NativeCreature enemy = combat.Enemies[index];
                enemy.SetCurrentHpInternal(Math.Clamp(currentHps[index], 0, enemy.MaxHp));
            }
        }
        else
        {
            _earlyOriginalMaxHp = _earlyMaxHpGuard ? [] : null;
            foreach (NativeCreature enemy in combat.Enemies)
            {
                if (!enemy.IsAlive)
                    continue;
                if (_earlyMaxHpGuard)
                {
                    _earlyOriginalMaxHp!.Add((enemy, enemy.MaxHp));
                    enemy.SetMaxHpInternal(999_999_999);
                    enemy.SetCurrentHpInternal(999_999_999);
                }
                else
                {
                    enemy.SetCurrentHpInternal(Math.Min(_earlyEnemyCurrentHp, enemy.MaxHp));
                }
            }
        }
        _earlyEnemyCurrentHp = 0;
        _earlyInitialEnemyCurrentHps = null;
        _earlyMaxHpGuard = false;
    }

    private static void AddCards(HashSet<CardModel> cards, CardPile pile)
    {
        foreach (CardModel card in pile.Cards)
            cards.Add(card);
    }
}
