// Ported from CombatSolver src/Flash/Bridge/FlashStateProjections.cs at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors), replacing its PredictionStateStore reads
// with direct native model reads at the root boundary. The cases and slot meanings are preserved.
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

using Creature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;

namespace Sts2Solver.Import;

/// <summary>
/// Projects per-model private state into <see cref="Power"/> aux slots and <see cref="Relic"/>
/// counters. Every root-private entry the old bridge captured is either read here or explicitly
/// rejected; a missing native field is a <see cref="ImportReason.MissingPrivateField"/> rejection.
/// </summary>
internal sealed class BattleProjections(NativeHistory history, PrivateStateLedger ledger)
{
    private readonly HashSet<(Creature Creature, string Name)> _claimedMonsterInts = [];

    public void ProjectMonster(Creature creature, MonsterDef def, ref Enemy target)
    {
        // Waterfall Giant expresses SteamPhase with two native moves: TriggerAboutToBlowState
        // installs ABOUT_TO_BLOW_MOVE, and its follow-up is EXPLODE_MOVE. Both are stable boundary
        // states, so project the phase instead of dropping a state the kernel models.
        target.SteamPhase = def.NativeId == "WATERFALL_GIANT"
            ? NativeAccess.CurrentMoveId(creature.Monster!) switch
            {
                "ABOUT_TO_BLOW_MOVE" => SteamPhase.AboutToBlow,
                "EXPLODE_MOVE" => SteamPhase.Explode,
                _ => SteamPhase.None,
            }
            : SteamPhase.None;
        if (def.AiRegisterNames.Count > Limits.EnemyAiRegisters)
            throw new UnsupportedImportException(ImportReason.Capacity,
                $"{creature.Monster?.Id.Entry} AI 寄存器 {def.AiRegisterNames.Count}");
        for (int register = 0; register < def.AiRegisterNames.Count; register++)
        {
            string name = def.AiRegisterNames[register];
            _claimedMonsterInts.Add((creature, name));
            target.Ai[register] = NativeAccess.MonsterInt(creature.Monster!, name);
        }
        foreach (string name in def.InertMonsterIntNames)
            _claimedMonsterInts.Add((creature, name));
    }

    public void ProjectPower(PowerModel power, Creature player, ref Power target)
    {
        bool privateStateClaimed = true;
        switch (power)
        {
            case JugglingPower juggling:
                target.Aux0 = NativeAccess.PowerData<JugglingPower.Data>(juggling, "attacksPlayedThisTurn").attacksPlayedThisTurn;
                break;
            case DarkEmbracePower darkEmbrace:
                target.Aux0 = NativeAccess.PowerData<DarkEmbracePower.Data>(darkEmbrace, "etherealCount").etherealCount;
                break;
            case CrimsonMantlePower crimsonMantle:
                target.Aux0 = crimsonMantle.DynamicVars["SelfDamage"].IntValue;
                break;
            case InfernoPower inferno:
                target.Aux0 = inferno.DynamicVars["SelfDamage"].IntValue;
                break;
            case SlowPower slow:
                target.Aux0 = slow.DynamicVars["SlowAmount"].IntValue;
                break;
            case SlothPower sloth:
                target.Aux0 = sloth._cardsPlayedThisTurn;
                break;
            case SkittishPower skittish:
                target.Aux0 = skittish.HasGainedBlockThisTurn ? 1 : 0;
                break;
            case VoidFormPower voidForm:
                target.Aux0 = NativeAccess.PowerData<VoidFormPower.Data>(voidForm, "cardsPlayedThisTurn").cardsPlayedThisTurn;
                break;
            case HardenedShellPower hardened:
                target.Aux0 = (int)NativeAccess.PowerData<HardenedShellPower.Data>(hardened, "damageReceivedThisTurn").damageReceivedThisTurn;
                break;
            case ThieveryPower thievery:
                target.Aux0 = thievery.DynamicVars.Gold.IntValue;
                break;
            // At a stable root the armed-attack states are idle and have no store entry.
            case GigantificationPower:
            case VigorPower:
            case RupturePower:
            case CalamityPower:
            case AfterimagePower:
            case StranglePower:
            case StormPower:
            case SubroutinePower:
            case OblivionPower:
            case SerpentFormPower:
                break;
            case HellraiserPower hellraiser:
                target.Aux0 = NativeAccess.PowerData<HellraiserPower.Data>(hellraiser, "infiniteAutoPlaysThisTurn").infiniteAutoPlaysThisTurn;
                break;
            case FeralPower feral:
                target.Aux0 = NativeAccess.PowerData<FeralPower.Data>(feral, "zeroCostAttacksPlayed").zeroCostAttacksPlayed;
                break;
            case SurroundedPower surrounded:
                target.Aux0 = (int)surrounded.Facing;
                break;
            // No root-captured store entry exists for CurlUp; the old bridge projects false/false.
            case CurlUpPower:
                target.Aux0 = 0;
                target.Aux1 = 0;
                break;
            case ChainsOfBindingPower chains:
                target.Aux0 = NativeAccess.PowerData<ChainsOfBindingPower.Data>(chains, "boundCardPlayed").boundCardPlayed ? 1 : 0;
                target.Aux1 = history.BoundCardsAfflictedThisTurn(chains);
                break;
            case WitheringPresencePower withering:
                target.Aux0 = withering.DynamicVars["CardsLeft"].IntValue;
                break;
            case PossessStrengthPower:
                target.Aux0 = NativeAccess.PossessedAmount(power, player, "stolenStrength");
                break;
            case PossessSpeedPower:
                target.Aux0 = NativeAccess.PossessedAmount(power, player, "stolenDexterity");
                break;
            case NemesisPower nemesis:
                target.Aux0 = NativeAccess.NemesisShouldApplyIntangible(nemesis) ? 1 : 0;
                break;
            case TenderPower tender:
                target.Aux0 = NativeAccess.TenderCardsPlayed(tender);
                break;
            case AutomationPower automation:
                target.Aux0 = automation.DisplayAmount;
                break;
            case PanachePower panache:
                target.Aux0 = panache.DynamicVars["CardsLeft"].IntValue;
                target.Aux1 = NativeAccess.PowerData<PanachePower.Data>(panache, "alreadyApplied").alreadyApplied ? 1 : 0;
                break;
            case TheBombPower bomb:
                target.Aux0 = bomb.DynamicVars.Damage.IntValue;
                break;
            case ToricToughnessPower toric:
            {
                decimal block = toric.DynamicVars.Block.BaseValue;
                if (block != decimal.Truncate(block) || block < 0m || block > int.MaxValue)
                    throw new UnsupportedImportException(ImportReason.Unrepresentable,
                        $"ToricToughness 格挡量 {block} 不是可表示的整数");
                target.Aux0 = (int)block;
                break;
            }
            case RollingBoulderPower:
                break;
            case MonologuePower monologue:
                target.Aux0 = monologue.DynamicVars.Strength.IntValue;
                target.Aux1 = monologue.DynamicVars[MonologuePower.strengthAppliedKey].IntValue;
                break;
            case OrbitPower orbit:
            {
                int remainder = (4 - orbit.DisplayAmount) % 4;
                if (remainder is < 0 or > 3)
                    throw new UnsupportedImportException(ImportReason.Unrepresentable, $"Orbit 剩余能量 {remainder}");
                target.Aux0 = remainder;
                break;
            }
            case PaleBlueDotPower paleBlueDot:
                target.Aux0 = NativeAccess.PaleBlueDotActivated(paleBlueDot) ? 1 : 0;
                break;
            case NightmarePower nightmare:
                if (NativeAccess.NightmareSelection(nightmare) == null)
                    throw new UnsupportedImportException(ImportReason.Unrepresentable, "Nightmare 选定卡缺失");
                break;
            // These two are captured by the old root bridge but have no projection in this batch.
            // Leaving them unclaimed turns the root into an explicit rejection instead of dropping
            // their private state.
            case SoulboundPower:
            case CacophonyPower:
                privateStateClaimed = false;
                break;
            // RagePower has no private state.
            default:
                break;
        }
        if (privateStateClaimed)
            ledger.Claim(power);
    }

    public void ProjectRelic(RelicModel relic, ref Relic target)
    {
        ledger.Claim(relic);
        switch (relic)
        {
            case ArtOfWar:
                (target.Counter, target.Aux) = Stateful(relic);
                break;
            case RuinedHelmet:
                if (Stateful(relic).Current != 0)
                    target.Flags |= RelicFlags.UsedThisCombat;
                break;
            case Vambrace vambrace:
                target.Counter = vambrace._blockGainedThisCombat ? 1 : 0;
                break;
            case PhilosophersStone stone:
                // AfterCreatureAddedToCombat applies the canonical StrengthPower var to each new
                // enemy. The value is not in the compiled relic var table, so freeze it in Aux for
                // the spawn path (PhilosophersStone.cs's only combat-run model constant).
                target.Aux = stone.DynamicVars["StrengthPower"].IntValue;
                break;
            case UnsettlingLamp:
                target.Counter = Stateful(relic).Current != 0 ? 1 : 0;
                break;
            case BurningSticks sticks:
                target.Counter = sticks._wasUsedThisCombat ? 1 : 0;
                break;
            case CentennialPuzzle puzzle:
                target.Counter = puzzle.UsedThisCombat ? 1 : 0;
                break;
            case LavaLamp lavaLamp:
                target.Counter = lavaLamp.TookDamageThisCombat ? 1 : 0;
                break;
            case DemonTongue tongue:
                target.Counter = tongue._triggeredThisTurn ? 1 : 0;
                break;
            case BeltBuckle:
                target.Counter = Stateful(relic).Current;
                break;
            case HappyFlower flower:
                target.Counter = flower.TurnsSeen;
                break;
            case JossPaper paper:
                target.Counter = paper.CardsExhausted;
                target.Aux = paper._etherealCount;
                break;
            case Kunai kunai:
                target.Counter = kunai._attacksPlayedThisTurn;
                break;
            case Kusarigama kusarigama:
                target.Counter = kusarigama._attacksPlayedThisTurn;
                break;
            case LetterOpener opener:
                target.Counter = opener._skillsPlayedThisTurn;
                break;
            case Nunchaku nunchaku:
                target.Counter = nunchaku.AttacksPlayed;
                break;
            case OrnamentalFan fan:
                target.Counter = fan._attacksPlayedThisTurn;
                break;
            case Shuriken shuriken:
                target.Counter = shuriken._attacksPlayedThisTurn;
                break;
            case TuningFork fork:
                target.Counter = fork.SkillsPlayed;
                break;
            // Orichalcum/FakeOrichalcum latch only in the branch state store, never at root capture.
            case Orichalcum:
            case FakeOrichalcum:
                target.Counter = 0;
                break;
            case PenNib penNib:
                target.Counter = penNib.AttacksPlayed;
                break;
            case Pendulum:
                target.Counter = Stateful(relic).Current;
                break;
            case Permafrost permafrost:
                target.Counter = permafrost._activatedThisCombat ? 1 : 0;
                break;
            case Pocketwatch:
                (target.Counter, target.Aux) = Stateful(relic);
                break;
            case RainbowRing ring:
                target.Counter = ring._activationCountThisTurn;
                target.Aux = ring._powersPlayedThisTurn;
                break;
            case VenerableTeaSet:
                target.Counter = Stateful(relic).Current;
                break;
            case BeatingRemnant remnant:
                target.Counter = (int)remnant._damageReceivedThisTurn;
                break;
            case LizardTail tail:
                target.Counter = tail.WasUsed ? 1 : 0;
                break;
            case RedSkull:
                target.Counter = Stateful(relic).Current;
                break;
            case FakeHappyFlower:
                target.Counter = Stateful(relic).Current;
                break;
            case MusicBox musicBox:
                if (musicBox._wasUsedThisTurn)
                    target.Counter = 1;
                break;
            case ThrowingAxe axe:
                target.Counter = axe._usedThisCombat ? 1 : 0;
                break;
            case Metronome metronome:
                target.Counter = metronome._orbsChanneled;
                break;
            case PaelsLegion paelsLegion:
                if (paelsLegion._affectedCardPlay != null
                    && !history.WasCardPlayFinished(paelsLegion._affectedCardPlay))
                {
                    throw new UnsupportedImportException(ImportReason.Unrepresentable,
                        "PaelsLegion 记录的 CardPlay 未完成");
                }
                target.Counter = paelsLegion._cooldown;
                if (paelsLegion._triggeredBlockLastTurn)
                    target.Flags |= RelicFlags.UsedThisCombat;
                break;
            case EmotionChip:
                (target.Counter, target.Aux) = Stateful(relic);
                break;
            case PaelsEye:
                (target.Counter, target.Aux) = Stateful(relic);
                break;
            case FakeVenerableTeaSet:
                target.Counter = Stateful(relic).Current;
                break;
            case BoneTea:
                target.Counter = Stateful(relic).Current;
                break;
            case PollinousCore:
                target.Counter = Stateful(relic).Current;
                break;
            case PaelsTears:
                target.Counter = Stateful(relic).Current;
                break;
            case IronClub club:
                target.Counter = club.CardsPlayed;
                break;
            case VelvetChoker choker:
                target.Counter = choker._cardsPlayedThisTurn;
                break;
            case PumpkinCandle candle:
                target.Counter = candle.KindleCount;
                break;
            case GalacticDust:
                target.Counter = Stateful(relic).Current;
                break;
            case MiniRegent:
                target.Counter = Stateful(relic).Current;
                break;
            case BrilliantScarf scarf:
                target.Counter = scarf._cardsPlayedThisTurn;
                break;
            case Regalite regalite:
                target.Counter = regalite._usedThisTurn ? 1 : 0;
                break;
            default:
                break;
        }
    }

    public void RequireAllMonsterIntsClaimed(IEnumerable<(Creature Creature, string Name, int Value)> entries)
    {
        foreach ((Creature creature, string name, int _) in entries)
        {
            if (!_claimedMonsterInts.Contains((creature, name)))
                throw new UnsupportedImportException(ImportReason.UnclaimedPrivateState,
                    $"MonsterInt: {creature.Monster?.Id.Entry}.{name}");
        }
    }

    private (int Current, int Previous) Stateful(RelicModel relic)
        => relic switch
        {
            ArtOfWar art => (art._anyAttacksPlayedThisTurn ? 1 : 0, art._anyAttacksPlayedLastTurn ? 1 : 0),
            BeltBuckle buckle => (buckle._dexterityApplied ? 1 : 0, 0),
            BoneTea tea => (tea.CombatsLeft, 0),
            EmotionChip chip => CaptureEmotionChip(chip),
            FakeHappyFlower flower => (flower.TurnsSeen, 0),
            FakeVenerableTeaSet tea => (tea.GainEnergyInNextCombat ? 1 : 0, 0),
            HappyFlower flower => (flower.TurnsSeen, 0),
            MiniRegent regent => (regent._usedThisTurn ? 1 : 0, 0),
            GalacticDust dust => (dust.StarsSpent, 0),
            PaelsEye eye => (eye._usedThisCombat ? 1 : 0, eye._wasOwnerPartOfLastPlayerTurn ? 1 : 0),
            PaelsTears tears => (tears._hadLeftoverEnergy ? 1 : 0, 0),
            RainbowRing ring => (ring._activationCountThisTurn, 0),
            RedSkull skull => (skull._strengthApplied ? 1 : 0, 0),
            Pendulum pendulum => (pendulum.TurnsSeen, 0),
            Pocketwatch pocketwatch => (pocketwatch._cardsPlayedThisTurn, pocketwatch._cardsPlayedLastTurn),
            PollinousCore core => (core.TurnsSeen, 0),
            RuinedHelmet helmet => (NativeAccess.RuinedHelmetUsed(helmet) ? 1 : 0, 0),
            UnsettlingLamp lamp => (NativeAccess.UnsettlingLampFinished(lamp) ? 1 : 0, 0),
            VenerableTeaSet tea => (tea.GainEnergyInNextCombat ? 1 : 0, 0),
            _ => (0, 0),
        };

    private (int Current, int Previous) CaptureEmotionChip(EmotionChip chip)
    {
        (bool last, bool current) = history.EmotionChipDamage(chip);
        return (last ? 1 : 0, current ? 1 : 0);
    }
}
