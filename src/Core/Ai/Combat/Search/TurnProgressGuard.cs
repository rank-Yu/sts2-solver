using System.Runtime.CompilerServices;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    /// <summary>
    /// A route may keep playing through a monotone loop until this many consecutive actions have
    /// produced no progress in the current player turn. The next no-progress card play is then not
    /// generated; end-turn children and every progressing play remain available. The bound is an
    /// explicit search boundary, not a kernel rule: ordinary turns stay far below it (the D2 corpus
    /// tops out at 18 actions in an optimal turn and 22 searched actions), while a pure block/draw
    /// loop is cut without limiting a legitimate attack loop.
    /// </summary>
    private const int MaxConsecutiveNoProgressActions = 256;

    /// <summary>
    /// Always-added headroom for enemy poison/doom lines. Native enemies can heal during their own
    /// turn (Regen, attached segments, Revive phases) and artifact can absorb a poison application.
    /// The extra max-HP-sized allowance keeps this line deliberately above the simple current-HP
    /// rule; a too-high line only searches longer, while a too-low line could discard a winning
    /// route. Constants are documented here because the line has to remain an upper bound.
    /// </summary>
    private const int EnemyProgressMarginFloor = 8;

    [InlineArray(Limits.Enemies)]
    private struct EnemyProgressValues
    {
        private int _first;
    }

    /// <summary>
    /// Per-enemy turn highs. Only instances with a nonzero poison or doom value are appended to the
    /// route metadata store; most searches carry the zero id and pay no per-node detail storage.
    /// </summary>
    private struct EnemyProgressHighs
    {
        public EnemyProgressValues Poison;
        public EnemyProgressValues Doom;
    }

    /// <summary>
    /// Turn-scoped route history. It lives on the route node only: no field is added to
    /// <see cref="Battle"/>, the state key or the fingerprint. <c>EnemyHighsId</c> references the
    /// route store's sparse per-enemy detail table; 0 means every poison/doom high is zero.
    /// </summary>
    private readonly record struct TurnProgressState(
        int EnemyHpBlockLow,
        ushort BlockHigh,
        short StrengthHigh,
        ushort PlayerHpHigh,
        ushort ConsecutiveNoProgress,
        int EnemyHighsId);

    private readonly record struct TurnProgressEvaluation(
        TurnProgressState State,
        EnemyProgressHighs EnemyHighs,
        bool EnemyHighsChanged,
        bool HasProgress);

    private static readonly ushort BarricadePowerId = PowerIdByName("BARRICADE_POWER");
    private static readonly ushort BlurPowerId = PowerIdByName("BLUR_POWER");
    private static readonly ushort BarricadeCardId = CardIdByName("BARRICADE");
    private static readonly ushort BlurCardId = CardIdByName("BLUR");
    private static readonly ushort SturdyClampRelicId = RelicIdByName("STURDY_CLAMP");
    private static readonly ushort DiamondDiademRelicId = RelicIdByName("DIAMOND_DIADEM");
    private static readonly ushort ThornsPowerId = PowerIdByName("THORNS_POWER");
    private static readonly ushort FlameBarrierPowerId = PowerIdByName("FLAME_BARRIER_POWER");
    private static readonly ushort ReflectPowerId = PowerIdByName("REFLECT_POWER");

    private static ushort CardIdByName(string nativeId)
        => Registry.TryCard(nativeId, out ushort id) ? id : ushort.MaxValue;

    private static ushort PowerIdByName(string nativeId)
        => Registry.TryPower(nativeId, out ushort id) ? id : ushort.MaxValue;

    private static ushort RelicIdByName(string nativeId)
        => Registry.TryRelic(nativeId, out ushort id) ? id : ushort.MaxValue;

    /// <summary>
    /// Captures the route baseline at a root or at a player-turn transition. Nothing here reads a
    /// live value: the battle, the frozen rules and the route's parent metadata are the only inputs.
    /// </summary>
    private static TurnProgressState InitialTurnProgress(in Battle battle)
        => new(
            NarrowEnemyHpBlockLow(EnemyHpBlockTotal(in battle)),
            NarrowHigh(battle.Player.Block),
            NarrowStrength(PlayerStrength(battle)),
            NarrowHigh(battle.Player.CurrentHp),
            ConsecutiveNoProgress: 0,
            EnemyHighsId: 0);

    /// <summary>Route-node storage is a hot budget: five scalar highs/counters fit in 16 bytes.</summary>
    private static ushort NarrowHigh(int value)
        => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

    private static short NarrowStrength(int value)
        => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    private static int NarrowEnemyHpBlockLow(long value)
        => (int)Math.Min(int.MaxValue, Math.Max(0, value));

    private static EnemyProgressHighs CaptureEnemyProgressHighs(in Battle battle)
    {
        EnemyProgressHighs highs = default;
        ushort poison = Registry.TryPower("POISON_POWER", out ushort poisonId) ? poisonId : ushort.MaxValue;
        ushort doom = Registry.TryPower("DOOM_POWER", out ushort doomId) ? doomId : ushort.MaxValue;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            byte owner = Kernel.EnemyOwner(index);
            if (poison != ushort.MaxValue)
                highs.Poison[index] = Math.Max(0, Kernel.GetAmount(in battle, owner, poison));
            if (doom != ushort.MaxValue)
                highs.Doom[index] = Math.Max(0, Kernel.GetAmount(in battle, owner, doom));
        }
        return highs;
    }

    private static bool EnemyProgressHighsEqual(
        in EnemyProgressHighs left, in EnemyProgressHighs right)
    {
        for (int index = 0; index < Limits.Enemies; index++)
        {
            if (left.Poison[index] != right.Poison[index]
                || left.Doom[index] != right.Doom[index])
            {
                return false;
            }
        }
        return true;
    }

    private static bool EnemyProgressHighsEmpty(in EnemyProgressHighs highs)
    {
        for (int index = 0; index < Limits.Enemies; index++)
        {
            if (highs.Poison[index] != 0 || highs.Doom[index] != 0)
                return false;
        }
        return true;
    }

    private static long EnemyHpBlockTotal(in Battle battle)
    {
        long total = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Creature creature = ref battle.Enemies[index].Creature;
            total += Math.Max(0, creature.CurrentHp) + Math.Max(0, creature.Block);
        }
        return total;
    }

    private static long LargestEnemyHpBlock(in Battle battle)
    {
        long largest = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Creature creature = ref battle.Enemies[index].Creature;
            long value = Math.Max(0, creature.CurrentHp) + Math.Max(0, creature.Block);
            if (value > largest)
                largest = value;
        }
        return largest;
    }

    private static int PlayerStrength(in Battle battle)
    {
        if (!Registry.TryPower("STRENGTH_POWER", out ushort strengthId))
            return 0;
        long total = 0;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner == Limits.PlayerOwner && power.Id == strengthId)
                total += power.Amount;
        }
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    /// <summary>
    /// The next state's turn-scoped progress. Enemy HP+block is the primary monotone objective;
    /// block, Strength, poison, doom and player HP only count while their own conservative line has
    /// not been reached. Every line is deliberately an upper bound.
    /// </summary>
    private TurnProgressEvaluation EvaluateTurnProgress(in Battle parent,
        in TurnProgressState parentProgress, in EnemyProgressHighs parentHighs,
        in Battle child, in PlayerAction action)
    {
        if (action.Kind == PlayerActionKind.EndTurn
            || child.PlayerTurn != parent.PlayerTurn)
        {
            EnemyProgressHighs resetHighs = CaptureEnemyProgressHighs(in child);
            TurnProgressState reset = InitialTurnProgress(in child) with
            {
                EnemyHighsId = parentProgress.EnemyHighsId,
            };
            return new TurnProgressEvaluation(
                reset, resetHighs,
                EnemyHighsChanged: !EnemyProgressHighsEqual(in parentHighs, in resetHighs),
                HasProgress: false);
        }

        EnemyProgressHighs highs = parentHighs;
        bool changedHighs = false;
        bool progress = false;
        int enemyTotal = NarrowEnemyHpBlockLow(EnemyHpBlockTotal(in child));
        int enemyLow = parentProgress.EnemyHpBlockLow;
        if (enemyTotal < enemyLow)
        {
            enemyLow = enemyTotal;
            progress = true;
        }

        int block = Math.Max(0, child.Player.Block);
        ushort blockValue = NarrowHigh(block);
        ushort blockHigh = parentProgress.BlockHigh;
        if (blockValue > blockHigh)
        {
            blockHigh = blockValue;
            if (TryBlockProgressLine(in child, out long blockLine) && block < blockLine)
                progress = true;
        }

        int strength = PlayerStrength(in child);
        short strengthValue = NarrowStrength(strength);
        short strengthHigh = parentProgress.StrengthHigh;
        if (strengthValue > strengthHigh)
        {
            strengthHigh = strengthValue;
            long line = StrengthProgressLine(in child);
            if (strength < line)
                progress = true;
        }

        ushort poison = Registry.TryPower("POISON_POWER", out ushort poisonId) ? poisonId : ushort.MaxValue;
        ushort doom = Registry.TryPower("DOOM_POWER", out ushort doomId) ? doomId : ushort.MaxValue;
        for (int index = 0; index < child.EnemyCount; index++)
        {
            ref readonly Creature creature = ref child.Enemies[index].Creature;
            long enemyLine = Math.Max(0, creature.CurrentHp)
                + Math.Max(EnemyProgressMarginFloor, creature.MaxHp);
            byte owner = Kernel.EnemyOwner(index);
            if (poison != ushort.MaxValue)
            {
                int amount = Math.Max(0, Kernel.GetAmount(in child, owner, poison));
                if (amount > highs.Poison[index])
                {
                    highs.Poison[index] = amount;
                    changedHighs = true;
                    if (amount < enemyLine)
                        progress = true;
                }
            }
            if (doom != ushort.MaxValue)
            {
                int amount = Math.Max(0, Kernel.GetAmount(in child, owner, doom));
                if (amount > highs.Doom[index])
                {
                    highs.Doom[index] = amount;
                    changedHighs = true;
                    if (amount < enemyLine)
                        progress = true;
                }
            }
        }

        int playerHp = child.Player.CurrentHp;
        ushort playerHpValue = NarrowHigh(playerHp);
        ushort playerHpHigh = parentProgress.PlayerHpHigh;
        if (playerHpValue > playerHpHigh)
        {
            playerHpHigh = playerHpValue;
            if (playerHp < child.Player.MaxHp)
                progress = true;
        }

        int consecutive = progress
            ? 0
            : Math.Min(MaxConsecutiveNoProgressActions,
                parentProgress.ConsecutiveNoProgress + 1);
        TurnProgressState state = new(
            enemyLow,
            blockHigh,
            strengthHigh,
            playerHpHigh,
            (ushort)consecutive,
            parentProgress.EnemyHighsId);
        return new TurnProgressEvaluation(
            state, highs, changedHighs, progress);
    }

    /// <summary>
    /// Whether a new block high is progress. A block-retaining effect (Barricade, Blur, Sturdy Clamp
    /// or Diamond Diadem) makes the line unbounded because block carries into later turns; the same
    /// is true when a known enemy counterattack can add blockable damage while the player keeps
    /// playing. Body Slam-style effects raise the line to the whole enemy HP+block total. The EH1
    /// threat projection supplies every blockable turn-end and enemy-turn hit, independent of the
    /// block currently held.
    /// </summary>
    private bool TryBlockProgressLine(in Battle battle, out long line)
    {
        line = 0;
        if (Kernel.IsOverOrEnding(in battle) || HasBlockRetention(in battle)
            || HasReactiveBlockableDamage(in battle))
        {
            return false;
        }
        long blockable = _scorer.ProjectHpAfterThreat(in battle).BlockableDamage;
        long enemyTotal = EnemyHpBlockTotal(in battle);
        line = _scorer.RulesForCodec.BodySlamCardId != ushort.MaxValue
            && HasCard(in battle, _scorer.RulesForCodec.BodySlamCardId)
                ? Math.Max(blockable, enemyTotal)
                : blockable;
        return true;
    }

    private static bool HasBlockRetention(in Battle battle)
    {
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner != Limits.PlayerOwner || power.Amount <= 0)
                continue;
            if (power.Id == BarricadePowerId || power.Id == BlurPowerId)
                return true;
        }
        for (int index = 0; index < battle.RelicCount; index++)
        {
            ref readonly Relic relic = ref battle.Relics[index];
            if ((relic.Flags & RelicFlags.Melted) != 0)
                continue;
            if (relic.Id == SturdyClampRelicId || relic.Id == DiamondDiademRelicId)
                return true;
        }
        return HasCard(in battle, BarricadeCardId) || HasCard(in battle, BlurCardId);
    }

    private static bool HasReactiveBlockableDamage(in Battle battle)
    {
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Amount <= 0 || power.Owner == Limits.PlayerOwner)
                continue;
            if (power.Id == ThornsPowerId || power.Id == FlameBarrierPowerId
                || power.Id == ReflectPowerId)
            {
                return true;
            }
        }
        return false;
    }

    private static long StrengthProgressLine(in Battle battle)
    {
        long largest = LargestEnemyHpBlock(in battle);
        // Weak multiplies the player's powered damage by 0.75. Dividing the enemy total by 0.75
        // makes the line the Strength needed to finish the largest enemy even through Weak.
        return (largest * 4L + 2L) / 3L;
    }

    private static bool HasCard(in Battle battle, ushort id)
    {
        if (id == ushort.MaxValue)
            return false;
        for (int index = 0; index < battle.CardCount; index++)
        {
            if (battle.Cards[index].Id == id)
                return true;
        }
        return false;
    }
}
