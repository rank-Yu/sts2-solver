using Sts2Solver.Ai.Combat;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

/// <summary>Minimal hand-built contracts for the 640-card capacity and card-position width.</summary>
internal static class Cards640Checks
{
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidDataException("cards-640: " + message); }

    internal static void Check(Rules rules)
    {
        CheckPacked640();
        CheckIndexSelection(rules);
        CheckCardListenerPastByte(rules);
    }

    private static void CheckPacked640()
    {
        Battle state = default;
        state.Round = 3;
        state.MaxHandSize = 10;
        for (int index = 0; index < Limits.Cards; index++)
            Piles.Append(ref state, Pile.Draw, new Card { Id = 0, Private = checked((short)(index & 0x7FFF)) });
        Assert(state.CardCount == Limits.Cards, $"640 张状态实际 {state.CardCount}");
        Assert(Battle.PackedSize(in state) == Battle.HeaderSize + Limits.Cards * 92,
            "640 张打包前缀长度");

        Battle copy = default;
        Battle.CopyPacked(in state, ref copy);
        Assert(Fingerprint.Equal(in copy, in state), "640 张 CopyPacked 往返");
        Assert(Fingerprint.Hash(in copy) == Fingerprint.Hash(in state), "640 张指纹往返");

        byte[] encoded = BattleCodec.Encode(state);
        Assert(encoded.Length == BattleCodec.HeaderSize + Battle.PackedSize(in state),
            "640 张编码长度");
        Battle decoded = BattleCodec.Decode(encoded);
        Assert(Fingerprint.Equal(in decoded, in state), "640 张解包往返");

        bool threw = false;
        try
        {
            Piles.Append(ref state, Pile.Draw, new Card { Id = 0, Private = 641 });
        }
        catch (CapacityException error)
        {
            threw = error.Region == "Cards";
        }
        Assert(threw, "第 641 张应抛 Cards CapacityException");
        Assert(state.CardCount == Limits.Cards, "失败插入不应改变计数");
    }

    private static void CheckIndexSelection(Rules rules)
    {
        Battle state = default;
        state.Round = 1;
        state.MaxHandSize = 10;
        state.Energy = 3;
        state.Player = new Creature { CurrentHp = 50, MaxHp = 50, Flags = CreatureFlags.Present };
        for (int index = 0; index < 300; index++)
            Piles.Append(ref state, Pile.Draw, new Card { Id = 0 });
        Assert(state.CardCount > Limits.LegacyCards, "索引选择需要总牌数超过 255");

        ChoiceRequest request = new()
        {
            Kind = ChoiceKind.SelectCards,
            Effect = ChoiceEffect.Discard,
            Source = Pile.Draw,
            OptionCount = 65,
            MinCount = 1,
            MaxCount = 1,
            IndexSelection = true,
        };
        Span<ulong> enumerated = stackalloc ulong[65];
        int count = ChoiceEnumerator.Enumerate(request, enumerated);
        Assert(count == 65, $"IndexSelection 应枚举 65 项，实际 {count}");
        Assert(enumerated[0] == 0 && enumerated[64] == 64, "IndexSelection 枚举值应为选项下标");

        Exec exact = new(ref state, rules, PlayerAction.EndTurn().WithChoice(17));
        Span<int> options = stackalloc int[65];
        for (int index = 0; index < options.Length; index++) options[index] = index;
        Span<int> selected = stackalloc int[1];
        bool taken = Kernel.ChooseCardsInRange(ref exact, ChoiceEffect.Discard, Pile.Draw,
            options, 1, 1, selected, out int selectedCount);
        Assert(taken && selectedCount == 1 && selected[0] == 17,
            "IndexSelection 应选中选项 17");

        bool multiThrew = false;
        Exec wide = new(ref state, rules, PlayerAction.EndTurn());
        try
        {
            Kernel.ChooseCardsInRange(ref wide, ChoiceEffect.Discard, Pile.Draw,
                options, 1, 2, selected, out _);
        }
        catch (CapacityException error)
        {
            multiThrew = error.Region == "Choice.Options";
        }
        Assert(multiThrew, "多选超过 64 项仍应抛 Choice.Options CapacityException");
    }

    private static void CheckCardListenerPastByte(Rules rules)
    {
        if (!Registry.TryCard("VOID", out ushort voidId))
            throw new InvalidDataException("cards-640: 缺少 VOID 测试牌");
        Battle state = default;
        state.Round = 1;
        state.MaxHandSize = 10;
        state.Energy = 3;
        state.Flags = BattleFlags.PlayerHooksActive;
        state.Player = new Creature { CurrentHp = 50, MaxHp = 50, Flags = CreatureFlags.Present };
        state.EnemyCount = 1;
        state.RosterCount = 1;
        state.Roster[0] = 0;
        state.Enemies[0].Creature = new Creature { CurrentHp = 10, MaxHp = 10, Flags = CreatureFlags.Present };
        for (int index = 0; index < 300; index++)
            Piles.Append(ref state, Pile.Draw, new Card { Id = 0 });
        Piles.Append(ref state, Pile.Draw, new Card { Id = voidId });
        Assert(state.CardCount == 301, "监听者测试状态应为 301 张");

        Exec x = new(ref state, rules, PlayerAction.EndTurn());
        AfterCardDrawnHook hook = new() { DrawnPosition = 300, FromHandDraw = false };
        HookDispatch.Dispatch(ref x, ref hook, gatedByEnding: true);
        Assert(x.B.Energy == 2, $"位置 300 的牌监听者应被收集并触发（能量应为 2，实际 {x.B.Energy}）");
    }
}
