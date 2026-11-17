using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.TestHost;

namespace Differential;

// Exercises native relic snapshots against the root-owned roster and the serialized Battle.
internal static class RelicImportChecks
{
    internal static int Run(string path)
    {
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();
        OfflineRequest request = JsonSerializer.Deserialize<OfflineRequest>(
            File.ReadAllText(path), OfflineJson.Options)!;
        Task<CombatState> build = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request,
            turnSetupSelector: new TurnSetupCardSelector(0));
        loop.RunUntilCompleted(build, TimeSpan.FromSeconds(120), "relic import audit root");
        CombatState state = build.GetAwaiter().GetResult();
        var player = LocalContext.GetMe(state)!;
        var run = (RunState)state.RunState;
        Console.WriteLine($"RELIC_CONTEXT character={player.Character.Id.Entry} room={run.CurrentRoom?.RoomType} mode={request.GameModeForTest}");
        Require(run.CurrentRoom?.RoomType == request.TargetRoomType, "actual room differs from requested room");
        int passed = 0, failed = 0, unavailable = 0;
        foreach (RelicModel canonical in ModelDb.AllRelics.OrderBy(r => r.Id.Entry, StringComparer.Ordinal))
        {
            if (!Registry.TryRelic(canonical.Id.Entry, out ushort id))
            {
                unavailable++;
                Console.WriteLine($"RELIC_UNREGISTERED {canonical.Id.Entry}");
                continue;
            }
            foreach (string variant in StateMembers.ContainsKey(canonical.GetType().Name)
                ? new[] { "fresh", "melted", "primed", "primed-melted" }
                : new[] { "fresh", "melted" })
            {
                RelicModel relic = canonical.ToMutable();
                player.AddRelicInternal(relic);
                try
                {
                    if (variant.StartsWith("primed", StringComparison.Ordinal))
                        foreach (string member in StateMembers[relic.GetType().Name])
                            SetMember(relic, member);
                    if (variant.EndsWith("melted", StringComparison.Ordinal))
                    {
                        relic.IsWax = true;
                        relic.IsMelted = true;
                    }
                    Rules rules = RulesCompiler.Compile(state, player, run);
                    Battle battle = BattleImporter.Import(state, player, rules);
                    int index = player.Relics.Count - 1;
                    Relic expected = new() { Id = id, Flags = relic.IsMelted ? RelicFlags.Melted : RelicFlags.None };
                    PrivateStateLedger ledger = PrivateStateLedger.Capture(state, player);
                    new BattleProjections(NativeHistory.Capture(state), ledger).ProjectRelic(relic, ref expected);
                    Relic actual = rules.Relics.Read(battle, index);
                    Require(actual.Id == expected.Id && actual.Counter == expected.Counter
                        && actual.Aux == expected.Aux && actual.Flags == expected.Flags, "projected values lost in roster");
                    byte[] bytes = BattleCodec.Encode(battle, rules.Relics);
                    Battle restored = BattleCodec.Decode(bytes, out RelicRoster roster);
                    Require(Fingerprint.Equal(battle, restored) && rules.Relics.Equivalent(roster), "snapshot round trip");
                    Rules recompiled = RulesCompiler.CompileAfterPrefix(state, player, run, battle);
                    Require(rules.Relics.Equivalent(recompiled.Relics), "post-prefix roster differs");
                    passed++;
                    Console.WriteLine($"RELIC_IMPORT_OK id={canonical.Id.Entry} variant={variant} state={Registry.Relic(id).HasCombatState} counter={actual.Counter} aux={actual.Aux} flags={actual.Flags}");
                }
                catch (InvalidDataException error) { Fail(error.Message); }
                catch (UnsupportedImportException error) { Fail(error.Message); }
                catch (UnsupportedException error) { Fail(error.Message); }
                catch (CapacityException error) { Fail(error.Message); }
                finally { player.RemoveRelicInternal(relic, silent: true); }

                void Fail(string message)
                {
                    failed++;
                    Console.WriteLine("RELIC_IMPORT_FAIL " + JsonSerializer.Serialize(new { id = canonical.Id.Entry, variant, message }));
                }
            }
        }
        Console.WriteLine($"RELIC_IMPORT_SUMMARY passed={passed} failed={failed} unregistered={unavailable}");
        return failed == 0 ? 0 : 1;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
    }

    // Nonzero states of the native fields read by BattleProjections; no acquisition or combat
    // hooks are fabricated. Each variant gets a new native relic instance and a fresh root.
    private static readonly Dictionary<string, string[]> StateMembers = new(StringComparer.Ordinal)
    {
        ["ArtOfWar"] = ["_anyAttacksPlayedThisTurn", "_anyAttacksPlayedLastTurn"],
        ["BeatingRemnant"] = ["_damageReceivedThisTurn"],
        ["BeltBuckle"] = ["_dexterityApplied"],
        ["BoneTea"] = ["CombatsLeft"],
        ["BrilliantScarf"] = ["_cardsPlayedThisTurn"],
        ["BurningSticks"] = ["_wasUsedThisCombat"],
        ["CentennialPuzzle"] = ["UsedThisCombat"],
        ["ConductorsBaton"] = ["_triggeredThisTurn"],
        ["DemonTongue"] = ["_triggeredThisTurn"],
        ["FakeHappyFlower"] = ["TurnsSeen"],
        ["FakeVenerableTeaSet"] = ["GainEnergyInNextCombat"],
        ["GalacticDust"] = ["StarsSpent"],
        ["HappyFlower"] = ["TurnsSeen"],
        ["IronClub"] = ["CardsPlayed"],
        ["JossPaper"] = ["CardsExhausted", "_etherealCount"],
        ["Kunai"] = ["_attacksPlayedThisTurn"],
        ["Kusarigama"] = ["_attacksPlayedThisTurn"],
        ["LavaLamp"] = ["TookDamageThisCombat"],
        ["LetterOpener"] = ["_skillsPlayedThisTurn"],
        ["LizardTail"] = ["WasUsed"],
        ["Metronome"] = ["_orbsChanneled"],
        ["MiniRegent"] = ["_usedThisTurn"],
        ["MusicBox"] = ["_wasUsedThisTurn"],
        ["Nunchaku"] = ["AttacksPlayed"],
        ["OrnamentalFan"] = ["_attacksPlayedThisTurn"],
        ["PaelsEye"] = ["_usedThisCombat", "_wasOwnerPartOfLastPlayerTurn"],
        ["PaelsLegion"] = ["_cooldown", "_triggeredBlockLastTurn"],
        ["PaelsTears"] = ["_hadLeftoverEnergy"],
        ["Pendulum"] = ["TurnsSeen"],
        ["PenNib"] = ["AttacksPlayed"],
        ["Permafrost"] = ["_activatedThisCombat"],
        ["Pocketwatch"] = ["_cardsPlayedThisTurn", "_cardsPlayedLastTurn"],
        ["PollinousCore"] = ["TurnsSeen"],
        ["PumpkinCandle"] = ["KindleCount"],
        ["RainbowRing"] = ["_activationCountThisTurn", "_powersPlayedThisTurn"],
        ["RedSkull"] = ["_strengthApplied"],
        ["Regalite"] = ["_usedThisTurn"],
        ["RuinedHelmet"] = ["_usedThisCombat"],
        ["Shuriken"] = ["_attacksPlayedThisTurn"],
        ["ThrowingAxe"] = ["_usedThisCombat"],
        ["TuningFork"] = ["SkillsPlayed"],
        ["UnsettlingLamp"] = ["_isFinishedTriggering"],
        ["Vambrace"] = ["_blockGainedThisCombat"],
        ["VenerableTeaSet"] = ["GainEnergyInNextCombat"],
    };

    private static void SetMember(RelicModel relic, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type type = relic.GetType();
        PropertyInfo? property = type.GetProperty(name, flags);
        FieldInfo? field = type.GetField(name, flags);
        Type valueType = property?.PropertyType ?? field?.FieldType ?? throw new MissingMemberException(type.Name, name);
        object value = valueType == typeof(bool) ? true
            : valueType == typeof(int) ? (object)1
            : valueType == typeof(decimal) ? (object)1m
            : throw new InvalidDataException($"Unexpected state type {type.Name}.{name}: {valueType}");
        if (property != null) property.SetValue(relic, value);
        else field!.SetValue(relic, value);
    }
}
