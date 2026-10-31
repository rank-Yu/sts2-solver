
using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

internal static class RetrievalAndWitherContracts
{
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidDataException("retrieval-wither: " + message); }

    private static SearchCase Read(string root)
    {
        using FileStream file = File.OpenRead($"tests/Golden/data/d1/{root}/config.br");
        using BrotliStream zip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new(); zip.CopyTo(bytes);
        return SearchCodec.DecodeRequest(bytes.ToArray());
    }

    internal static void Run()
    {
        CheckWither();
        CheckRetrieval();
        Console.WriteLine("RETRIEVAL_WITHER hand/block/terminal/ordinary/direct/later-turn/disabled/unpayable=pass");
    }

    private static void CheckWither()
    {
        SearchCase item = Read("a3-aeonglass-boss");
        Scorer scorer = item.Options.Scorer!;
        Battle root = item.Battles[0];
        root.PowerCount = 0; root.RelicCount = 0;
        root.Player.CurrentHp = root.Player.MaxHp = 300;
        root.Player.Block = 0;
        for (int slot = 0; slot < root.RosterCount; slot++)
        {
            ref Enemy enemy = ref root.Enemies[root.Roster[slot]];
            MonsterNode[] nodes = item.Rules.MonsterRule(enemy.Rules).Nodes;
            enemy.Move = checked((byte)Array.FindIndex(nodes, n => n.Attacks.Length > 0));
            enemy.Flags &= ~EnemyFlags.SkipNextMove;
        }
        Assert(Registry.TryCard("WITHER", out ushort id), "Wither registered");
        Card blank = Kernel.CreateCard(item.Rules, id);
        blank.Var0 = checked((short)-(int)item.Rules.Card(id, 0).Vars[0]);
        int first = root.HandCount;
        Piles.Append(ref root, Pile.Hand, blank);
        Piles.Append(ref root, Pile.Hand, blank);
        double Score(Battle b) => scorer.Features(b, 0).Score;
        Battle six = root;
        six.Cards[first].Var0 += 3; six.Cards[first + 1].Var0 += 3;
        Assert(Score(six) - Score(root) == -6 * ScoreWeights.Hp, "sum current damage of both hand cards");
        int incoming = root.Player.CurrentHp - scorer.Features(root, 0).ProjectedPlayerHp;
        Assert(incoming > 0, "fixture has an attack");
        Battle covered = root, coveredSix = six;
        covered.Player.Block = coveredSix.Player.Block = incoming + 10;
        Assert(Score(covered) == Score(coveredSix), "sufficient leftover block covers Wither");
        covered.Player.Block = coveredSix.Player.Block = incoming + 2;
        Assert(Score(coveredSix) - Score(covered) == -4 * ScoreWeights.Hp,
            "only two block remain after attack; do not reuse block per Wither");
        foreach (Pile pile in new[] { Pile.Draw, Pile.Discard })
        {
            Battle zeroPile = root, sixPile = six;
            for (int i = 0; i < 2; i++)
            {
                Card zero = Piles.RemoveAt(ref zeroPile, Pile.Hand, first);
                Card damage = Piles.RemoveAt(ref sixPile, Pile.Hand, first);
                Piles.Append(ref zeroPile, pile, zero); Piles.Append(ref sixPile, pile, damage);
            }
            Assert(Score(zeroPile) == Score(sixPile), "no extra non-hand damage penalty: " + pile);
        }
        foreach (Outcome outcome in new[] { Outcome.Victory, Outcome.Defeat, Outcome.EventDefeat })
        {
            Battle a = root, b = six; a.Outcome = b.Outcome = outcome;
            if (outcome == Outcome.Defeat) a.Player.CurrentHp = b.Player.CurrentHp = 0;
            Assert(Score(a) == Score(b), "no outstanding damage after terminal: " + outcome);
        }
        byte[] before = BattleCodec.Encode(six);
        _ = Score(six);
        Assert(before.AsSpan().SequenceEqual(BattleCodec.Encode(six)), "scoring does not mutate battle");
    }

    private static void CheckRetrieval()
    {
        SearchCase item = Read("dx7-droplet-fetched-power");
        Battle root = item.Battles[0];
        Assert(Registry.TryCard("DEFRAGMENT", out ushort powerId), "Defragment registered");
        int start = Piles.Start(root, Pile.Draw);
        int position = Enumerable.Range(0, root.DrawCount).Single(i => root.Cards[start + i].Id == powerId);
        Card power = Piles.RemoveAt(ref root, Pile.Draw, position);
        Piles.Append(ref root, Pile.Draw, power); // Still available to fetch after the next draw.
        Trace Run(bool enabled, int turns, bool noEnergy = false)
        {
            Battle battle = root; if (noEnergy) battle.Energy = 0;
            Trace trace = new(battle, item.Rules, powerId);
            BeamOptions options = SearchBudgetPolicy.ApplyRegression(item.Options) with
            {
                BeamWidth = 64, MaxTurns = turns, MaxExpandedNodes = 100_000, UnlimitedNodes = false,
                SoftTimeBudget = TimeSpan.MaxValue, Act3BossStrategy = enabled, Trace = trace,
                Bound = null,
                Ordering = new(battle.Player.CurrentHp, battle.Player.MaxHp, BossHpRelief.None, PostCombatRelicHealProfile.None),
                Potions = new(int.MinValue, 1, new(PotionUseMode.Smart, []), PotionUseMode.Smart, false),
            };
            _ = new BeamSolver(item.Rules, options).Solve([battle], CancellationToken.None);
            return trace;
        }
        Trace active = Run(true, 2);
        Assert(active.Direct > 0, "direct continuation actually expanded");
        Assert(active.LaterDirect > 0, "direct continuation after first searched turn");
        Assert(active.OrdinaryEndTurn > 0, "ordinary fetched state still expands EndTurn");
        Assert(Run(false, 1).Direct == 0, "disabled encounter has no direct continuation");
        Assert(Run(true, 1, noEnergy: true).Direct == 0, "unpayable power has no direct continuation");
    }

    private sealed class Trace : BeamTrace
    {
        private readonly Dictionary<int, Battle> _states = [];
        private readonly Dictionary<int, PlayerAction> _actions = [];
        private readonly HashSet<int> _inserted = [];
        private readonly HashSet<int> _selected = [];
        private readonly Rules _rules;
        private readonly ushort _powerId;
        private readonly int _initialTurn;
        private int _parent;
        private PlayerAction _action;
        internal int Direct, LaterDirect, OrdinaryEndTurn;
        internal Trace(Battle root, Rules rules, ushort powerId)
        { _states[0] = root; _rules = rules; _powerId = powerId; _initialTurn = root.PlayerTurn; }
        internal override int Match(int parentNode, in PlayerAction action)
        { _parent = parentNode; _action = action; return 1; }
        internal override bool IsWatched(int node) => node > 0;
        internal override void NodeCreated(int node, int step)
        {
            Battle before = _states[_parent], after = before;
            Assert(Kernel.Step(ref after, _action, _rules, out _, out _) == StepResult.Completed,
                "trace action is complete");
            if (_actions.TryGetValue(_parent, out PlayerAction parentAction)
                && parentAction.Kind == PlayerActionKind.UsePotion)
            {
                if (!_selected.Contains(_parent))
                {
                    Assert(_inserted.Contains(_parent), "ordinary fetched state inserted before direct child");
                    Assert(_action.Kind == PlayerActionKind.PlayCard
                        && before.Cards[_action.HandIndex].Id == _powerId,
                        "only fetched power is expanded ahead of ordinary selection");
                    Direct++;
                    if (before.PlayerTurn > _initialTurn) LaterDirect++;
                }
                else if (_action.Kind == PlayerActionKind.EndTurn) OrdinaryEndTurn++;
            }
            _states[node] = after; _actions[node] = _action;
        }
        internal override void NodeInserted(int node, bool added, int existingNode, bool incomingWon,
            double rank, double existingRank, double score, double existingScore,
            int existingPotionCount, int existingTurn, int existingDepth, int existingEnemyHp,
            int existingProjectedHp, in TraceRouteInfo targetInfo, in TraceRouteInfo existingInfo)
        { _inserted.Add(node); }
        internal override void NodeRetained(int node, string phase, bool selected, bool required,
            int rankPosition, int count, int width, string lanes)
        { if (selected) _selected.Add(node); }
        internal override void SetupLeader(string lane, in TraceSetupLeader target,
            in TraceSetupLeader leader, bool targetBeatsLeader, string comparison) { }
        internal override void NodePruned(int node, string phase, string reason) { }
        internal override void StandPatProbe(int node, bool selected, bool available, bool allEnemiesDead,
            int delayedDamage, int projectedHp, int resourceValue, int laneValue, double score, bool chosen) { }
        internal override void Finish(string reason, int turns, int expanded) { }
    }
}
