// Field-level reconciliation over the Battle encoding. Permanently dead enemies are tombstoned by
// the native game (its move state machine is reset and the monster leaves CombatState.Enemies), so
// their action-machine fields are normalized on both sides before the byte comparison. This is a
// state-surface normalization, not a tolerance: every other byte must match.

using System.Runtime.CompilerServices;

namespace Sts2Solver.Simulation.Combat;

internal static class BattleReconciler
{
    internal const int MaxDiffLines = 24;

    internal static bool Same(in Battle expected, in Battle actual)
    {
        Battle left = Normalize(in expected);
        Battle right = Normalize(in actual);
        return Fingerprint.Equal(in left, in right);
    }

    internal static string[] Diff(in Battle expected, in Battle actual)
    {
        Battle left = Normalize(in expected);
        Battle right = Normalize(in actual);
        List<string> lines = BattleDump.Diff(left, right, "predicted", "live");
        for (int index = 0; index < lines.Count; index++)
            lines[index] = AnnotatePowerId(lines[index], in left);
        if (lines.Count > MaxDiffLines)
        {
            lines.RemoveRange(MaxDiffLines, lines.Count - MaxDiffLines);
            lines.Add($"... 还有更多差异（已截断到 {MaxDiffLines} 行）");
        }
        return [.. lines];
    }

    /// <summary>Adds the predicted power's native id to a <c>Power[i].Field</c> dump line.</summary>
    private static string AnnotatePowerId(string line, in Battle battle)
    {
        int start = line.IndexOf("Power[", StringComparison.Ordinal);
        if (start < 0)
            return line;
        int digits = start + "Power[".Length;
        int end = line.IndexOf(']', digits);
        if (end < 0 || !int.TryParse(line.AsSpan(digits, end - digits), out int row)
            || (uint)row >= (uint)battle.PowerCount)
        {
            return line;
        }
        string nativeId = Registry.Power(battle.Powers[row].Id).NativeId;
        return line[..end] + ":" + nativeId + line[end..];
    }

    internal static Battle Normalize(in Battle battle)
    {
        Unsafe.SkipInit(out Battle copy);
        Battle.CopyPacked(in battle, ref copy);
        // The action-local shuffle counter is carried from the route by LiveCombatTracker because
        // the native game does not expose it. Zero it in the comparison view so a carried value
        // cannot hide a real card-pile/RNG divergence.
        copy.ShuffleEventCount = 0;
        // TurnStartChoicePauses records how the host will answer later turn-start choices (its
        // turn-setup takeover mode), which the player can change between a search and its boundary
        // (pressing 执行本场战斗 with another solver loaded). It is not native state; whatever it
        // changed in the simulated state is still compared through the bytes it produced.
        copy.Flags &= ~BattleFlags.TurnStartChoicePauses;
        // A detached card is a value snapshot, not a pile card that entered combat. The importer's
        // one-time power-affliction entry marker is not observable for it, and the kernel's clone
        // semantics clear it; normalize it on both sides.
        for (int slot = 0; slot < Limits.DetachedCards; slot++)
        {
            ref Card detached = ref copy.DetachedCards[slot];
            if (!detached.IsDetachedCardPresent)
                continue;
            // An id-0 card keeps the presence bit: it is the only distinction from an unused slot.
            if (detached.Id != 0)
                detached.Flags &= ~CardFlags.PowerAfflictionChecked;
        }
        for (int index = 0; index < copy.EnemyCount; index++)
        {
            ref Enemy enemy = ref copy.Enemies[index];
            if (!IsPermanentTombstone(in enemy))
                continue;
            // A removed permanent corpse cannot take another move. Native drops its transient
            // stun skip bit; preserve that bit everywhere it is still observable.
            bool inRoster = false;
            for (int position = 0; position < copy.RosterCount; position++)
                inRoster |= copy.Roster[position] == index;
            if (enemy.DeathPhase == DeathPhase.PermanentlyDead && enemy.Creature.CurrentHp <= 0
                && (enemy.Flags & EnemyFlags.InCombat) == 0 && !inRoster)
                enemy.Flags &= ~EnemyFlags.SkipNextMove;
            enemy.Move = 0;
            enemy.ForcedMove = byte.MaxValue;
            enemy.LoggedMoves = 0;
            enemy.EverUsedMoves = 0;
            enemy.PoweredAttackHitsThisTurn = 0;
            enemy.SteamPhase = SteamPhase.None;
            enemy.RecentMoves = default;
            enemy.Ai = default;
        }
        // A permanently dead enemy is no longer in the native roster. Native keeps some power rows
        // on the removed creature (MinionPower) while the old death cleanup removes most of them;
        // the row is inert either way. Compact every power owned by such a tombstone so table order
        // and count compare the living state only.
        int write = 0;
        for (int read = 0; read < copy.PowerCount; read++)
        {
            Power power = copy.Powers[read];
            if (IsPermanentlyDeadOwner(in copy, power.Owner))
                continue;
            PowerDef def = Registry.Power(power.Id);
            if ((power.Flags & PowerFlags.SkipNextTickFromMonster) != 0 && !def.TicksAtEnemySideEnd)
            {
                // ApplyFromMonster records a solver-side one-shot skip for powers outside the native
                // duration-skip set. A power that never reaches TickDurations (ChainsOfBinding) can
                // never consume it, and the native PowerModel has no such field, so the bit is
                // unreachable state; clear it on both sides. Powers that do tick keep it strict.
                power.Flags &= ~PowerFlags.SkipNextTickFromMonster;
            }
            copy.Powers[write++] = power;
        }
        for (int index = write; index < copy.PowerCount; index++)
            copy.Powers[index] = default;
        copy.PowerCount = checked((byte)write);
        return copy;
    }

    private static bool IsPermanentlyDeadOwner(in Battle battle, byte owner)
    {
        if (owner < Limits.FirstEnemyOwner || owner == Limits.NoOwner)
            return false;
        int index = owner - Limits.FirstEnemyOwner;
        return index < battle.EnemyCount && IsPermanentTombstone(in battle.Enemies[index]);
    }

    internal static bool IsPermanentTombstone(in Enemy enemy)
        => enemy.DeathPhase == DeathPhase.PermanentlyDead
            || (!enemy.Creature.IsAlive && (enemy.Flags & EnemyFlags.DeathProcessed) != 0);
}
