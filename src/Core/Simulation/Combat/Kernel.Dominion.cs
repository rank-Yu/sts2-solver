namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    private static void DominionAfterCreatureAdded(ref Exec x, byte owner)
    {
        DominionRules rules = x.R.Dominion;
        if (rules.SpawnAmount == 0) return;
        if (rules.Kind == DominionKind.WaterfallGiant && Registry.TryPower("MINION_POWER", out ushort minion)
            && GetAmount(x.B, owner, minion) > 0) return;
        ApplyPowerWithoutCardSource(ref x, owner, rules.SpawnPower, rules.SpawnAmount, Limits.NoOwner);
    }

    private static void DominionBeforeHandDraw(ref Exec x)
    {
        if (x.R.Dominion.Kind != DominionKind.KnowledgeDemon || x.B.PlayerTurn > 1 || x.B.Player.IsDead) return;
        if (!Registry.TryCard("SLOTH", out ushort sloth) || !Registry.TryCard("AMNESIA", out ushort amnesia))
            throw new UnsupportedException("知识恶魔领域缺少选项定义。");
        Span<Card> options = stackalloc Card[2];
        options[0] = CreateCard(x.R, sloth);
        options[1] = CreateCard(x.R, amnesia);
        if (!ChooseOption(ref x, ChoiceEffect.ApplyKnowledgeCurse, options, out int selected)) return;
        string nativePower = selected == 0 ? "SLOTH_POWER" : "AMNESIA_POWER";
        if (!Registry.TryPower(nativePower, out ushort power))
            throw new UnsupportedException($"知识恶魔领域缺少 {nativePower}。");
        ApplyPowerWithoutCardSource(ref x, Limits.PlayerOwner, power,
            (int)CardEffects.Var(x.B, x.R, options[selected], 0), Limits.PlayerOwner);
    }
}
