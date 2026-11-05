// Adapted from CombatSolver src/Search/PowerCardValuation/Projection/PowerCardMechanismFacts.cs
// and card projection files at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors).
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

/// <summary>Resolve canonical power IDs once; missing known content fails explicitly when used.</summary>
internal static class PowerEvidenceIds
{
    private readonly record struct Binding(string Name, ushort Id)
    {
        internal ushort Required => Id != ushort.MaxValue ? Id
            : throw new UnsupportedException($"能力证据缺少状态 {Name}。");
    }

    private static Binding Bind(string name) => new(name,
        Registry.TryPower(name, out ushort id) ? id : ushort.MaxValue);
    private static readonly Binding VulnerableBinding = Bind("VULNERABLE_POWER");
    internal static ushort Vulnerable => VulnerableBinding.Required;
    private static readonly Binding WeakBinding = Bind("WEAK_POWER");
    internal static ushort Weak => WeakBinding.Required;
    private static readonly Binding PoisonBinding = Bind("POISON_POWER");
    internal static ushort Poison => PoisonBinding.Required;
    private static readonly Binding AccelerantBinding = Bind("ACCELERANT_POWER");
    internal static ushort Accelerant => AccelerantBinding.Required;
    private static readonly Binding EnvenomBinding = Bind("ENVENOM_POWER");
    internal static ushort Envenom => EnvenomBinding.Required;
    private static readonly Binding FocusBinding = Bind("FOCUS_POWER");
    internal static ushort Focus => FocusBinding.Required;
    private static readonly Binding FurnaceBinding = Bind("FURNACE_POWER");
    internal static ushort Furnace => FurnaceBinding.Required;
    private static readonly Binding GenesisBinding = Bind("GENESIS_POWER");
    internal static ushort Genesis => GenesisBinding.Required;
    private static readonly Binding PlatingBinding = Bind("PLATING_POWER");
    internal static ushort Plating => PlatingBinding.Required;
    private static readonly Binding CountdownBinding = Bind("COUNTDOWN_POWER");
    internal static ushort Countdown => CountdownBinding.Required;
    private static readonly Binding StrengthBinding = Bind("STRENGTH_POWER");
    internal static ushort Strength => StrengthBinding.Required;
    private static readonly Binding DexterityBinding = Bind("DEXTERITY_POWER");
    internal static ushort Dexterity => DexterityBinding.Required;
    private static readonly Binding RollingBoulderBinding = Bind("ROLLING_BOULDER_POWER");
    internal static ushort RollingBoulder => RollingBoulderBinding.Required;
    private static readonly Binding ChildOfTheStarsBinding = Bind("CHILD_OF_THE_STARS_POWER");
    internal static ushort ChildOfTheStars => ChildOfTheStarsBinding.Required;
    private static readonly Binding FanOfKnivesBinding = Bind("FAN_OF_KNIVES_POWER");
    internal static ushort FanOfKnives => FanOfKnivesBinding.Required;
    private static readonly Binding IntangibleBinding = Bind("INTANGIBLE_POWER");
    internal static ushort Intangible => IntangibleBinding.Required;
    private static readonly Binding CrimsonMantleBinding = Bind("CRIMSON_MANTLE_POWER");
    internal static ushort CrimsonMantle => CrimsonMantleBinding.Required;
    private static readonly Binding CrueltyBinding = Bind("CRUELTY_POWER");
    internal static ushort Cruelty => CrueltyBinding.Required;
    private static readonly Binding DarkEmbraceBinding = Bind("DARK_EMBRACE_POWER");
    internal static ushort DarkEmbrace => DarkEmbraceBinding.Required;
    private static readonly Binding DemonFormBinding = Bind("DEMON_FORM_POWER");
    internal static ushort DemonForm => DemonFormBinding.Required;
    private static readonly Binding FeelNoPainBinding = Bind("FEEL_NO_PAIN_POWER");
    internal static ushort FeelNoPain => FeelNoPainBinding.Required;
    private static readonly Binding InfernoBinding = Bind("INFERNO_POWER");
    internal static ushort Inferno => InfernoBinding.Required;
    private static readonly Binding JuggernautBinding = Bind("JUGGERNAUT_POWER");
    internal static ushort Juggernaut => JuggernautBinding.Required;
    private static readonly Binding PyreBinding = Bind("PYRE_POWER");
    internal static ushort Pyre => PyreBinding.Required;
    private static readonly Binding RuptureBinding = Bind("RUPTURE_POWER");
    internal static ushort Rupture => RuptureBinding.Required;
    private static readonly Binding ViciousBinding = Bind("VICIOUS_POWER");
    internal static ushort Vicious => ViciousBinding.Required;
    private static readonly Binding NoxiousFumesBinding = Bind("NOXIOUS_FUMES_POWER");
    internal static ushort NoxiousFumes => NoxiousFumesBinding.Required;
    private static readonly Binding ShroudBinding = Bind("SHROUD_POWER");
    internal static ushort Shroud => ShroudBinding.Required;
    private static readonly Binding BufferBinding = Bind("BUFFER_POWER");
    internal static ushort Buffer => BufferBinding.Required;
    private static readonly Binding MasterPlannerBinding = Bind("MASTER_PLANNER_POWER");
    internal static ushort MasterPlanner => MasterPlannerBinding.Required;
    private static readonly Binding SubroutineBinding = Bind("SUBROUTINE_POWER");
    internal static ushort Subroutine => SubroutineBinding.Required;
    private static readonly Binding ToolsOfTheTradeBinding = Bind("TOOLS_OF_THE_TRADE_POWER");
    internal static ushort ToolsOfTheTrade => ToolsOfTheTradeBinding.Required;
    private static readonly Binding AfterimageBinding = Bind("AFTERIMAGE_POWER");
    internal static ushort Afterimage => AfterimageBinding.Required;
    private static readonly Binding AccuracyBinding = Bind("ACCURACY_POWER");
    internal static ushort Accuracy => AccuracyBinding.Required;
    private static readonly Binding PhantomBladesBinding = Bind("PHANTOM_BLADES_POWER");
    internal static ushort PhantomBlades => PhantomBladesBinding.Required;
    private static readonly Binding SpeedsterBinding = Bind("SPEEDSTER_POWER");
    internal static ushort Speedster => SpeedsterBinding.Required;
    private static readonly Binding TrackingBinding = Bind("TRACKING_POWER");
    internal static ushort Tracking => TrackingBinding.Required;
    private static readonly Binding WellLaidPlansBinding = Bind("WELL_LAID_PLANS_POWER");
    internal static ushort WellLaidPlans => WellLaidPlansBinding.Required;
}
