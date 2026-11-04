// Ported from CombatSolver/tools/search/OfflineSearchHarness/GameBootstrap.cs
// at commit a62f1850 (MIT, Copyright (c) 2026 Torch1230 and contributors).
// Adapted for the differential host: no CombatSolver assembly, no search-policy injection.
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace Differential;

/// <summary>
/// 游戏核心静态状态的离线初始化，以及所有「会打到 Godot 原生层」的绕过。
/// 不启动 Godot 可执行文件，也不建场景树。
/// </summary>
internal static class GameBootstrap
{
    public const string HarmonyId = "sts2solver.offline.host";
    private static Harmony? _harmony;
    private static readonly List<string> _bypasses = [];
    private static readonly LocTable EmptyLocTable = new("offline", []);

    public static IReadOnlyList<string> Bypasses => _bypasses;
    public static Harmony Harmony => _harmony ?? throw new InvalidOperationException("Harmony 未初始化。");

    /// <summary>第一步：只装绕过补丁，必须在任何游戏代码跑起来之前。</summary>
    public static void ApplyGodotBypasses()
    {
        _harmony = new Harmony(HarmonyId);

        Patch(typeof(Logger), "GetIsRunningFromGodotEditor", prefix: nameof(NotGodotEditorPrefix));
        Patch(typeof(ConsoleLogPrinter), nameof(ConsoleLogPrinter.Print), prefix: nameof(ConsolePrintPrefix));
        Patch(typeof(LocString), nameof(LocString.GetRawText), prefix: nameof(LocRawTextPrefix));
        Patch(typeof(LocString), nameof(LocString.GetFormattedText), prefix: nameof(LocFormattedTextPrefix));
        Patch(typeof(LocManager), nameof(LocManager.GetTable), prefix: nameof(EmptyLocTablePrefix),
            args: [typeof(string)]);
        Patch(typeof(LocString), nameof(LocString.Exists), prefix: nameof(LocExistsPrefix),
            args: [typeof(string), typeof(string)]);
        foreach (string loader in new[] { "LoadRunAssets", "LoadActAssets", "LoadRoomCombatAssets" })
            Patch(typeof(PreloadManager), loader, prefix: nameof(CompletedTaskPrefix));
        Patch(typeof(MegaCrit.Sts2.Core.Nodes.Ftue.NCombatRulesFtue),
            nameof(MegaCrit.Sts2.Core.Nodes.Ftue.NCombatRulesFtue.Create), prefix: nameof(NullResultPrefix),
            args: Type.EmptyTypes);
        Patch(typeof(MegaCrit.Sts2.Core.Saves.Migrations.MigrationRegistry),
            nameof(MegaCrit.Sts2.Core.Saves.Migrations.MigrationRegistry.RegisterAllMigrations),
            prefix: nameof(SkipPrefix));
        Patch(typeof(NGame), nameof(NGame.GetGameVersion), prefix: nameof(GameVersionPrefix));
        Patch(typeof(Godot.Time), nameof(Godot.Time.GetTicksMsec), prefix: nameof(TicksMsecPrefix));
        Patch(typeof(RunManager), nameof(RunManager.WriteReplay), prefix: nameof(SkipPrefix),
            args: [typeof(bool)]);
        Patch(typeof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet), nameof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet.Clear),
            prefix: nameof(SkipPrefix));
        // CardRewardAlternative's constructor builds Godot StringName hotkeys; TestMode never
        // shows the alternative buttons, so the offline host has no alternatives.
        Patch(typeof(MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative),
            nameof(MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative.Generate),
            prefix: nameof(NoCardRewardAlternativesPrefix));
        Patch(typeof(Godot.ResourceLoader), nameof(Godot.ResourceLoader.Load),
            prefix: nameof(NullResourcePrefix),
            args: [typeof(string), typeof(string), typeof(Godot.ResourceLoader.CacheMode)],
            generics: Type.EmptyTypes);
        Patch(typeof(MegaCrit.Sts2.Core.Models.Monsters.SoulNexus), "AfterDeath",
            prefix: nameof(SkipPrefix),
            args: [typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)]);
        foreach (Type monster in new[]
                 {
                     typeof(MegaCrit.Sts2.Core.Models.Monsters.Rocket),
                     typeof(MegaCrit.Sts2.Core.Models.Monsters.Crusher),
                 })
        {
            Patch(monster, "BeforeDeath", prefix: nameof(CompletedTaskPrefix),
                args: [typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)]);
        }
        Patch(typeof(MegaCrit.Sts2.Core.Platform.PlatformUtil),
            nameof(MegaCrit.Sts2.Core.Platform.PlatformUtil.GetPlatformBranch), prefix: nameof(PlatformBranchPrefix));
        Patch(typeof(MegaCrit.Sts2.Core.Platform.PlatformUtil),
            nameof(MegaCrit.Sts2.Core.Platform.PlatformUtil.GetPlayerNameRaw), prefix: nameof(PlayerNameRawPrefix));
        TurnSetupRootCapture.Install();
    }

    /// <summary>第二步：游戏静态状态。顺序照 OneTimeInitialization.ExecuteEssential。</summary>
    public static string InitializeStaticState()
    {
        AccessTools.Field(typeof(ReflectionHelper), "_modTypes").SetValue(null, Array.Empty<Type>());
        AccessTools.PropertySetter(typeof(ModManager), nameof(ModManager.State))
            ?.Invoke(null, [ModManagerState.Initialized]);

        TestMode.IsOn = true;
        Log.Info("[offline-host] logger probe");

        MemorySaveStore store = new();
        _ = new MegaCrit.Sts2.Core.Saves.Migrations.MigrationManager(store);
        SaveManager saveManager = new(store, forceSynchronous: true);
        SaveManager.MockInstanceForTesting(saveManager);
        saveManager.InitSettingsDataForTest();
        saveManager.InitPrefsDataForTest();
        // NGame calls InitProfileId after loading the profile. The offline host has no Godot
        // user:// path (UserDataPathProvider asks OS.HasFeature) and a pure memory store, so set
        // the id directly; otherwise caught save-progress calls log "InitProfileId must be called".
        AccessTools.Field(typeof(SaveManager), "_currentProfileId").SetValue(saveManager, 1);

        OfflineLocalization.Install("eng");
        AssemblyInfo.Init();
        ModelDb.Init(AbstractModelSubtypes.All.ToArray());
        ModelIdSerializationCache.Init();
        ModelDb.InitIds();
        MessageTypes.Initialize();
        ActionTypes.Initialize();
        _ = ModelDb.AllCards;
        _ = ModelDb.AllRelics;
        _ = ModelDb.AllPotions;
        _ = ModelDb.AllEncounters;

        AccessTools.Field(typeof(NGame), "_mainThreadId").SetValue(null, Environment.CurrentManagedThreadId);
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("NGame.IsMainThread() 仍为 false。");

        return $"models={ModelDb.All.Count()} cards={ModelDb.AllCards.Count()} "
            + $"encounters={ModelDb.All.OfType<EncounterModel>().Count()} "
            + $"characters={ModelDb.AllCharacters.Count()} main_thread={NGame.IsMainThread()}";
    }

    /// <summary>
    /// Godot 源生成的节点类型，其静态构造会造 <c>Godot.StringName</c>（MethodName/PropertyName 表），
    /// 离线没有 Godot 原生层。战斗逻辑里凡是取 <c>NXxx.Instance</c> 的地方都会触发这件事，
    /// 而离线本来就没有场景树、这些单例本来就该是 null。
    /// </summary>
    public static int SkipGodotNodeStaticConstructors()
    {
        int patched = 0;
        Type godotObject = typeof(Godot.GodotObject);
        MethodInfo prefix = typeof(GameBootstrap).GetMethod(
            nameof(SkipNodeStaticConstructorPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (Type type in typeof(AbstractModel).Assembly.GetTypes())
        {
            if (!godotObject.IsAssignableFrom(type) || type.ContainsGenericParameters)
                continue;
            ConstructorInfo? cctor = type.TypeInitializer;
            if (cctor == null)
                continue;
            Harmony.Patch(cctor, prefix: new HarmonyMethod(prefix));
            patched++;
        }
        _bypasses.Add($"跳过 {patched} 个 Godot 节点类型的静态构造");
        return patched;
    }

    private static bool SkipNodeStaticConstructorPrefix() => false;

    private static void Patch(Type type, string method, string prefix, Type[]? args = null,
        Type[]? generics = null)
    {
        MethodInfo target = args == null
            ? AccessTools.Method(type, method)
            : type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                    | BindingFlags.Instance)
                .FirstOrDefault(candidate => candidate.Name == method
                    && !candidate.IsGenericMethodDefinition
                    && ParametersMatch(candidate, args))
            ?? throw new MissingMethodException(type.FullName, method);
        MethodInfo replacement = typeof(GameBootstrap).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)!;
        Harmony.Patch(target, prefix: new HarmonyMethod(replacement));
        _bypasses.Add($"{type.Name}.{method}");
    }

    private static bool ParametersMatch(MethodInfo method, Type[] args)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != args.Length)
            return false;
        for (int index = 0; index < args.Length; index++)
        {
            if (parameters[index].ParameterType != args[index])
                return false;
        }
        return true;
    }

    private static bool SkipPrefix() => false;

    private static bool PlayerNameRawPrefix(ulong playerId, ref string __result)
    {
        __result = playerId switch
        {
            1uL => "Test Host",
            1000uL => "Test Client 1",
            2000uL => "Test Client 2",
            3000uL => "Test Client 3",
            _ => playerId.ToString(),
        };
        return false;
    }

    private static bool NoCardRewardAlternativesPrefix(
        MegaCrit.Sts2.Core.Rewards.CardReward cardReward,
        ref IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative> __result)
    {
        // The bypass must not hide a listener that would have added alternatives. PaelsWing and
        // future models override TryModifyCardRewardAlternatives; the offline host cannot build
        // the Godot hotkey objects those alternatives need, so fail loudly instead.
        Type baseType = typeof(MegaCrit.Sts2.Core.Models.AbstractModel);
        MethodInfo? baseMethod = baseType.GetMethod("TryModifyCardRewardAlternatives",
            BindingFlags.Instance | BindingFlags.Public);
        foreach (MegaCrit.Sts2.Core.Models.AbstractModel model in
                 cardReward.Player.RunState.IterateHookListeners(null))
        {
            MethodInfo? method = model.GetType().GetMethod("TryModifyCardRewardAlternatives",
                BindingFlags.Instance | BindingFlags.Public);
            if (method != null && method.DeclaringType != baseMethod?.DeclaringType)
            {
                throw new NotSupportedException(
                    $"离线宿主禁止隐藏卡牌奖励 alternatives：{model.Id} 重写了 " +
                    "TryModifyCardRewardAlternatives。");
            }
        }
        __result = [];
        return false;
    }

    private static bool NullResourcePrefix(ref Godot.Resource __result)
    {
        __result = null!;
        return false;
    }

    private static bool TicksMsecPrefix(ref ulong __result)
    {
        __result = (ulong)Environment.TickCount64;
        return false;
    }

    private static bool GameVersionPrefix(ref string __result)
    {
        __result = "offline-host";
        return false;
    }

    private static bool PlatformBranchPrefix(ref MegaCrit.Sts2.Core.Platform.PlatformBranch __result)
    {
        __result = MegaCrit.Sts2.Core.Platform.PlatformBranch.None;
        return false;
    }

    private static bool NotGodotEditorPrefix(ref bool __result)
    {
        __result = false;
        return false;
    }

    private static bool ConsolePrintPrefix(LogLevel logLevel, string text, int skipFrames)
    {
        if (logLevel >= LogLevel.Warn)
            Console.Error.WriteLine($"[{logLevel.ToString().ToUpperInvariant()}] {text}");
        return false;
    }

    private static bool LocRawTextPrefix(LocString __instance, ref string __result)
    {
        __result = __instance.LocEntryKey ?? string.Empty;
        return false;
    }

    private static bool LocFormattedTextPrefix(LocString __instance, ref string __result)
    {
        __result = __instance.LocEntryKey ?? string.Empty;
        return false;
    }

    private static bool EmptyLocTablePrefix(ref LocTable __result)
    {
        __result = EmptyLocTable;
        return false;
    }

    private static bool LocExistsPrefix(ref bool __result)
    {
        __result = true;
        return false;
    }

    private static bool CompletedTaskPrefix(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }

    private static bool NullResultPrefix(ref object? __result)
    {
        __result = null;
        return false;
    }
}

internal static class HarnessLog
{
    public static void Trace(string stage)
    {
        Console.Error.WriteLine($"[trace] {stage}");
        Console.Error.Flush();
    }
}
