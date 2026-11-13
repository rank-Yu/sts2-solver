// Native 0.113.0 arrow and melody state. Reference: CombatSolver
// src/Prediction/FoolStateSupport.cs at f48b69c590cb8b689ec7fdf033c2f62e625e0f1e
// (MIT, Copyright (c) 2026 Torch1230 and contributors).
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using Sts2Solver.Simulation.Combat;
using NativeCreature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;
using NativeCardTag = MegaCrit.Sts2.Core.Entities.Cards.CardTag;

namespace Sts2Solver.Import;

public static partial class BattleImporter
{
    private static void ImportFool(ref Battle battle, CombatState state, Player player,
        IReadOnlyList<NativeCreature> known, Rules rules)
    {
        var pcs = player.PlayerCombatState!;
        var arrows = pcs.Arrows.ToArray();
        if (arrows.Length > FoolState.ArrowCapacity)
            throw new UnsupportedImportException(ImportReason.Capacity, $"箭数量 {arrows.Length}");
        battle.Fool.ArrowCount = checked((byte)arrows.Length);
        for (int i = 0; i < arrows.Length; i++)
        {
            battle.Fool.Arrows[i].SetStatus((int)arrows[i].Status);
            battle.Fool.Arrows[i].DestroyAfterImpaled = arrows[i].DestroyAfterImpaled;
        }
        Dictionary<CardModel, int> cards = new(ReferenceEqualityComparer.Instance);
        int position = 0;
        foreach (var pile in new[] { pcs.Hand, pcs.DrawPile, pcs.DiscardPile, pcs.ExhaustPile, pcs.PlayPile })
            foreach (var card in pile.Cards) cards.Add(card, position++);
        foreach (var creature in new[] { player.Creature }.Concat(known))
        {
            byte owner = Owner(creature, player, known, player.Osty);
            int showOffOccurrence = 0;
            foreach (var power in creature.Powers)
            {
                if (power.Amount == 0) continue;
                if (power is ImpaledPower impaled)
                {
                    int row = Kernel.ImpaledPowerIndex(battle, owner);
                    if (row < 0) continue;
                    battle.Powers[row].Aux0 = impaled.HitCounter;
                    var source = impaled.Arrows.ToArray();
                    for (int i = 0; i < source.Length; i++)
                    {
                        int arrow = Array.IndexOf(arrows, source[i]);
                        if (arrow < 0) throw new UnsupportedImportException(ImportReason.Unrepresentable,
                            "ImpaledPower 引用了其他玩家的箭");
                        if (i == 0) battle.Powers[row].Aux1 = arrow + 1;
                        battle.Fool.Arrows[arrow].Next = i + 1 < source.Length
                            ? checked((byte)(Array.IndexOf(arrows, source[i + 1]) + 1)) : (byte)0;
                    }
                }
                if (power is ShowOffPower showOff)
                {
                    Registry.TryPower(showOff.Id.Entry, out ushort showOffId);
                    int occurrence = 0;
                    int row = -1;
                    for (int i = 0; i < battle.PowerCount; i++)
                        if (battle.Powers[i].Id == showOffId && battle.Powers[i].Owner == owner
                            && occurrence++ == showOffOccurrence) { row = i; break; }
                    showOffOccurrence++;
                    if (row < 0) throw new UnsupportedImportException(ImportReason.Unrepresentable, "炫技能力实例缺失");
                    var source = showOff.ShowOffInstance;
                    battle.Powers[row].Aux0 = source is null ? 0 : -1;
                    battle.Powers[row].Aux1 = source?.DeckVersion is null ? 0 : 1;
                    if (source is not null && cards.TryGetValue(source, out int slot))
                    {
                        battle.Cards[slot].FoolIdentity = checked((ushort)(slot + 1));
                        battle.Powers[row].Aux0 = slot + 1;
                    }
                }
                if (power is AimPower aim)
                {
                    Registry.TryPower(aim.Id.Entry, out ushort id);
                    int row = Kernel.FindPower(battle, owner, id);
                    var data = NativeAccess.PowerData<AimPower.Data>(aim, "commandToModify");
                    if (data.commandToModify is not null)
                    {
                        battle.Powers[row].Aux0 = -1;
                        if (data.commandToModify.ModelSource is CardModel card && cards.TryGetValue(card, out int slot))
                        {
                            battle.Cards[slot].FoolIdentity = checked((ushort)(slot + 1));
                            battle.Powers[row].Aux0 = slot + 1;
                        }
                    }
                }
            }
        }
        var history = CombatManager.Instance.History;
        NativeCardTag[] tags = [NativeCardTag.Wah, NativeCardTag.Cho, NativeCardTag.Ki];
        for (int i = 0; i < tags.Length; i++)
        {
            battle.Fool.Started[i] = history.CardPlaysStarted.Count(entry => entry.HappenedThisTurn(state)
                && entry.CardPlay.Player == player && entry.CardPlay.Card.Tags.Contains(tags[i]));
            battle.Fool.Finished[i] = history.CardPlaysFinished.Count(entry => entry.HappenedThisTurn(state)
                && entry.CardPlay.Player == player && entry.CardPlay.Card.Tags.Contains(tags[i]));
        }
        battle.Fool.MelodyCount = history.CardPlaysFinished.Count(entry => entry.HappenedThisTurn(state)
            && entry.CardPlay.Player == player && tags.Any(entry.CardPlay.Card.Tags.Contains));
        battle.Fool.SadnessGenerated = history.Entries.OfType<MegaCrit.Sts2.Core.Combat.History.Entries.CardGeneratedEntry>()
            .Count(entry => entry.Creator == player && entry.Card is MegaCrit.Sts2.Core.Models.Cards.Sadness);
        Kernel.NormalizeFoolState(ref battle);
    }
}
