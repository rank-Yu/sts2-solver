using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat.Policy;

/// <summary>Frozen queen opening context for kill-timing retention.</summary>
internal sealed record QueenSearchPolicy(byte QueenSlot, byte TorchSlot, ushort QueenId, ushort TorchId,
    ushort StrengthId, ushort DexterityId, ushort AfterimageId, ushort AccuracyId, ushort ToolsId,
    int StrengthThreshold, int InitialDexterity, int ProwessDexterity, int FootworkGain)
{
    internal const int QueenHpThreshold = 200;
    private const int SetupHpValue = 10;

    internal static QueenSearchPolicy? Capture(in Battle root, Rules rules)
    {
        if (root.PlayerTurn != 1 || root.Counter(Counter.CardsPlayedThisTurn) != 0
            || root.Outcome != Outcome.None || root.RosterCount != 2)
            return null;
        int queen = -1, torch = -1;
        for (int i = 0; i < root.EnemyCount; i++)
        {
            ref readonly Enemy enemy = ref root.Enemies[i];
            if (!enemy.Creature.IsAlive || (enemy.Flags & EnemyFlags.InCombat) == 0) continue;
            switch (Registry.Monster(enemy.MonsterId).NativeId)
            {
                case "QUEEN": queen = i; break;
                case "TORCH_HEAD_AMALGAM": torch = i; break;
            }
        }
        if (queen < 0 || torch < 0) return null;
        ushort Power(string id) => Registry.TryPower(id, out ushort value)
            ? value : throw new InvalidDataException("Missing queen strategy power: " + id);
        ushort strength = Power("STRENGTH_POWER"), dexterity = Power("DEXTERITY_POWER");
        int initialStrength = Amount(root, strength), initialDexterity = Amount(root, dexterity);
        int prowessStrength = 0, prowessDexterity = 0, footwork = 0;
        for (int i = 0; i < root.CardCount; i++)
        {
            ref readonly Card card = ref root.Cards[i];
            string id = Registry.Card(card.Id).NativeId;
            if (id is not ("PROWESS" or "FOOTWORK")) continue;
            // Modified dynamic variables need a separate audited policy.
            if (card.Private > 0 || card.Var0 != 0 || card.Var1 != 0) return null;
            CardStats stats = rules.Card(card.Id, card.Upgrade);
            if (id == "PROWESS")
            {
                prowessStrength = Math.Max(prowessStrength, checked((int)stats.Vars[0]));
                prowessDexterity = Math.Max(prowessDexterity, checked((int)stats.Vars[1]));
            }
            else footwork = Math.Max(footwork, checked((int)stats.Vars[0]));
        }
        return new((byte)queen, (byte)torch, root.Enemies[queen].MonsterId, root.Enemies[torch].MonsterId,
            strength, dexterity, Power("AFTERIMAGE_POWER"), Power("ACCURACY_POWER"), Power("TOOLS_OF_THE_TRADE_POWER"),
            prowessStrength > 0 ? checked(initialStrength + prowessStrength) : int.MaxValue,
            initialDexterity, prowessDexterity, footwork);
    }

    private static int Amount(in Battle battle, ushort id)
    {
        for (int i = 0; i < battle.PowerCount; i++)
            if (battle.Powers[i].Owner == Limits.PlayerOwner && battle.Powers[i].Id == id)
                return battle.Powers[i].Amount;
        return 0;
    }

    internal void Encode(BinaryWriter writer)
    {
        writer.Write(QueenSlot); writer.Write(TorchSlot); writer.Write(QueenId); writer.Write(TorchId);
        writer.Write(StrengthId); writer.Write(DexterityId); writer.Write(AfterimageId);
        writer.Write(AccuracyId); writer.Write(ToolsId); writer.Write(StrengthThreshold);
        writer.Write(InitialDexterity); writer.Write(ProwessDexterity); writer.Write(FootworkGain);
    }

    internal static QueenSearchPolicy Decode(BinaryReader reader)
    {
        QueenSearchPolicy policy = new(reader.ReadByte(), reader.ReadByte(), reader.ReadUInt16(), reader.ReadUInt16(),
            reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(),
            reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        if (policy.QueenSlot >= Limits.Enemies || policy.TorchSlot >= Limits.Enemies
            || policy.QueenSlot == policy.TorchSlot || policy.ProwessDexterity < 0 || policy.FootworkGain < 0)
            throw new InvalidDataException("Invalid queen search policy.");
        return policy;
    }

    internal void ValidateRoot(in Battle root)
    {
        if (QueenSlot >= root.EnemyCount || TorchSlot >= root.EnemyCount
            || root.Enemies[QueenSlot].MonsterId != QueenId || root.Enemies[TorchSlot].MonsterId != TorchId
            || Registry.Monster(QueenId).NativeId != "QUEEN"
            || Registry.Monster(TorchId).NativeId != "TORCH_HEAD_AMALGAM")
            throw new InvalidDataException("Queen search policy does not match its root.");
    }
}
