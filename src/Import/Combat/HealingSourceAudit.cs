// 0.113.0 native closure and current Loadout hooks, reviewed against
// CombatSolver b2a5c474 src/Search/StrategicHpRecoveryBound.Components.cs,
// src/Prediction/PredictionModHookSubscriberCapture.cs and the installed native/Loadout IL.
// MIT, Copyright (c) 2026 Torch1230 and contributors.
using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Blitz;
using MegaCrit.Sts2.Core.Models.Dread;
using MegaCrit.Sts2.Core.Models.Dread.Dominions;

namespace Sts2Solver.Import;

/// <summary>只在根导入时检查已审计版本及不会在战斗中引入回血的外部来源。</summary>
internal static class HealingSourceAudit
{
    // Game 0.113.0 / 16ac0fe3 has different module identities on each platform.
    private static readonly HashSet<Guid> NativeModules =
    [
        new("6e7e5a35-f319-4bcc-8b1d-f3d40358dadf"), // Linux
        new("8d868b33-7cb7-491b-9661-84ef422ff27c"), // macOS
        new("ad562b4e-7352-4ca2-ae45-0ff44cd284bc"), // Windows, upstream audit
    ];

    private static readonly Guid LoadoutModule = new("40fc7a62-b154-4b6f-8684-b2e73ad046c7");

    internal static bool IsCurrentNativeAssembly
        => NativeModules.Contains(typeof(AbstractModel).Module.ModuleVersionId);

    internal static bool IsNonHealingModeSource(AbstractModel source)
        => source is DreadRunModel or BlitzRunModel
            or AeonglassDominion or CeremonialBeastDominion or InsatiableDominion
            or KaiserCrabDominion or KinDominion or KnowledgeDemonDominion
            or LagavulinMatriarchDominion or QueenDominion or SoulFyshDominion
            or TestSubjectDominion or VantomDominion or WaterfallGiantDominion;

    internal static bool IsNonHealingSubscriber(AbstractModel source)
    {
        Type type = source.GetType();
        if (type.Module.ModuleVersionId != LoadoutModule)
            return false;
        return type.FullName switch
        {
            // These hooks finish before the captured root, or only modify hand/energy limits.
            "Loadout.Services.Morphing.BottledMonsterMorphCombatSnapshotHook"
                or "Loadout.Services.PowerGiver.PowerGiverCombatStartHook"
                or "Loadout.Services.TildeKey.LoadoutKillAllMonstersCombatHook"
                or "Loadout.Services.TildeKey.LoadoutEveryCardFreeCombatHook"
                or "Loadout.Services.TildeKey.LoadoutMaxHandSizeModifier" => true,
            "Loadout.Services.PowerGiver.PowerGiverSummonHook" => HasNoSummonPowers(type.Assembly),
            _ => false,
        };
    }

    private static bool HasNoSummonPowers(Assembly assembly)
    {
        // Read the exact state consumed by AfterCreatureAddedToCombat. Do not reload settings,
        // invoke setup hooks or allow configured powers to be mistaken for an empty callback.
        Type service = assembly.GetType("Loadout.Services.PowerGiver.PowerGiverStateService")!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        object sync = service.GetField("SyncRoot", flags)!.GetValue(null)!;
        lock (sync)
        {
            object run = service.GetField("_run", flags)!.GetValue(null)!;
            IDictionary counters = (IDictionary)run.GetType().GetProperty("MonsterCounters")!.GetValue(run)!;
            return counters.Count == 0;
        }
    }
}
