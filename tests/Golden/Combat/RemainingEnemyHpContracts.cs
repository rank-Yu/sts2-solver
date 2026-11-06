using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Contracts for the remaining-to-kill HP cap used by every damage power projection. The three
/// encounter-specific branches mirror the native death lifecycle:
/// Test Subject keeps its later forms' HP in the AI registers while AdaptablePower is alive,
/// Decimillipede restores ReattachPower's heal while another segment can still act, and secondary
/// enemies (MinionPower, including the reviving EyeWithTeeth and Parafright) are not required to
/// win because the last primary enemy's death ends combat and kills them.
/// </summary>
internal static class RemainingEnemyHpContracts
{
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidDataException("remaining-enemy-hp: " + message); }

    private static int Remaining(in Battle battle, Rules rules, Scorer scorer)
    {
        ScoreFeatures features = scorer.Features(battle, 0);
        PowerEvidence.View view = new(battle, features, rules, scorer);
        return view.RemainingEnemyHp();
    }

    private static (SearchCase Case, Battle Battle, Scorer Scorer) Load(string root)
    {
        SearchCase item = PowerEvidenceContracts.ReadRoot(root);
        Scorer scorer = item.Options.Scorer
            ?? throw new InvalidDataException("remaining-enemy-hp: missing scorer for " + root);
        return (item, item.Battles[0], scorer);
    }

    internal static void Run()
    {
        CheckTestSubjectForms();
        CheckDecimillipedeReattach();
        CheckMinions();
        CheckRevivingIllusionMinion();
        CheckAxebotStock();
        Console.WriteLine("REMAINING_ENEMY_HP forms/reattach/minions/stock=pass");
    }

    /// <summary>
    /// Axebot StockPower: each pending replacement adds the replacement rule's maximum HP plus 10 per
    /// further missing stock (native RespawnMaxHpBonus), so Stock 2 adds max + (max + 10).
    /// </summary>
    private static void CheckAxebotStock()
    {
        (SearchCase item, Battle battle, Scorer scorer) = Load("a3-axebots-normal");
        if (!Registry.TryPower("STOCK_POWER", out ushort stock)
            || !Registry.TryMonster("AXEBOT", out ushort axebot))
        {
            throw new InvalidDataException("remaining-enemy-hp: missing STOCK_POWER or AXEBOT");
        }
        int maximum = item.Rules.MonsterRule(checked((byte)item.Rules.SpawnRulesId(axebot))).InitialHpMax;
        int expected = 0;
        int stocked = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Enemy enemy = ref battle.Enemies[index];
            if (!enemy.Creature.IsAlive || (enemy.Flags & EnemyFlags.InCombat) == 0)
                continue;
            expected += enemy.Creature.CurrentHp;
            if (enemy.MonsterId != axebot)
                continue;
            int amount = Kernel.GetAmount(battle, Kernel.EnemyOwner((byte)index), stock);
            for (int replacement = 1; replacement <= amount; replacement++)
                expected += maximum + (1 - (amount - replacement)) * 10;
            stocked += amount;
        }
        Assert(stocked > 0, "Axebot root carries StockPower");
        Assert(Remaining(battle, item.Rules, scorer) == expected,
            $"stocked replacements count toward remaining HP: expected {expected}");
    }

    private static void CheckTestSubjectForms()
    {
        (SearchCase item, Battle battle, Scorer scorer) = Load("a3-test-subject-boss");
        Assert(battle.EnemyCount == 1, "Test Subject root has one enemy");
        ref readonly Enemy subject = ref battle.Enemies[0];
        Assert(Registry.Monster(subject.MonsterId).NativeId == "TEST_SUBJECT", "root is Test Subject");
        Assert(subject.Ai[0] == 0, "root starts on the first form");
        int second = subject.Ai[2];
        int third = subject.Ai[3];
        Assert(second > 0 && third > 0, "root carries both later form HP values");
        byte owner = Kernel.EnemyOwner(0);
        if (!Registry.TryPower("ADAPTABLE_POWER", out ushort adaptable))
            throw new InvalidDataException("remaining-enemy-hp: missing ADAPTABLE_POWER");
        Assert(Kernel.GetAmount(battle, owner, adaptable) > 0, "root has AdaptablePower");

        Battle first = item.Battles[0];
        first.Enemies[0].Creature.CurrentHp = 111;
        first.Enemies[0].Creature.MaxHp = 111;
        Assert(Remaining(first, item.Rules, scorer) == 111 + second + third,
            "first form adds both later form HP values");

        Battle secondForm = item.Battles[0];
        secondForm.Enemies[0].Ai[0] = 1;
        secondForm.Enemies[0].Creature.CurrentHp = second - 7;
        secondForm.Enemies[0].Creature.MaxHp = second;
        Assert(Remaining(secondForm, item.Rules, scorer) == second - 7 + third,
            "second form adds only the third form HP");

        Battle firstDead = item.Battles[0];
        firstDead.Enemies[0].Creature.CurrentHp = 0;
        firstDead.Enemies[0].DeathPhase = DeathPhase.Reviving;
        Assert(Remaining(firstDead, item.Rules, scorer) == second + third,
            "reviving first form still requires both later forms");

        Battle thirdForm = item.Battles[0];
        thirdForm.Enemies[0].Ai[0] = 2;
        thirdForm.Enemies[0].Creature.CurrentHp = third - 5;
        thirdForm.Enemies[0].Creature.MaxHp = third;
        Kernel.SetPowerAmount(ref thirdForm, owner, adaptable, 0);
        Assert(Remaining(thirdForm, item.Rules, scorer) == third - 5,
            "third form counts only its current HP");
    }

    private static void CheckDecimillipedeReattach()
    {
        (SearchCase item, Battle battle, Scorer scorer) = Load("km2-decimillipede");
        Assert(battle.EnemyCount == 3, "Decimillipede root has three segments");
        if (!Registry.TryPower("REATTACH_POWER", out ushort reattach))
            throw new InvalidDataException("remaining-enemy-hp: missing REATTACH_POWER");
        int first = battle.Enemies[0].Creature.CurrentHp;
        int second = battle.Enemies[1].Creature.CurrentHp;
        int third = battle.Enemies[2].Creature.CurrentHp;
        int heal = Kernel.GetAmount(battle, Kernel.EnemyOwner(0), reattach);
        Assert(first > 0 && second > 0 && third > 0 && heal > 0,
            "root carries three living segments and the reattach amount");
        Assert(Remaining(battle, item.Rules, scorer) == first + second + third,
            "living segments count only current HP");

        Battle oneReviving = item.Battles[0];
        oneReviving.Enemies[0].Creature.CurrentHp = 0;
        oneReviving.Enemies[0].DeathPhase = DeathPhase.Reviving;
        Assert(Remaining(oneReviving, item.Rules, scorer) == second + third + heal,
            "a reviving segment adds the reattach heal while another segment can act");

        Kernel.SetPowerAmount(ref oneReviving, Kernel.EnemyOwner(0), reattach, 17);
        Assert(Remaining(oneReviving, item.Rules, scorer) == second + third + 17,
            "the reattach heal is read from the branch power amount");

        Battle twoReviving = item.Battles[0];
        twoReviving.Enemies[0].Creature.CurrentHp = 0;
        twoReviving.Enemies[0].DeathPhase = DeathPhase.Reviving;
        twoReviving.Enemies[1].Creature.CurrentHp = 0;
        twoReviving.Enemies[1].DeathPhase = DeathPhase.Reviving;
        Assert(Remaining(twoReviving, item.Rules, scorer) == third + heal + heal,
            "every reviving segment adds its own reattach heal");

        Battle allDead = item.Battles[0];
        allDead.Enemies[0].Creature.CurrentHp = 0;
        allDead.Enemies[0].DeathPhase = DeathPhase.Reviving;
        allDead.Enemies[1].Creature.CurrentHp = 0;
        allDead.Enemies[1].DeathPhase = DeathPhase.PermanentlyDead;
        allDead.Enemies[2].Creature.CurrentHp = 0;
        allDead.Enemies[2].DeathPhase = DeathPhase.PermanentlyDead;
        Assert(Remaining(allDead, item.Rules, scorer) == 0,
            "a reviving segment contributes nothing when every other segment is permanently dead");
    }

    private static void CheckMinions()
    {
        (SearchCase item, Battle battle, Scorer scorer) = Load("p12-the-kin-boss");
        Assert(battle.EnemyCount == 3, "Kin root has two followers and the priest");
        int follower1 = battle.Enemies[0].Creature.CurrentHp;
        int follower2 = battle.Enemies[1].Creature.CurrentHp;
        int priest = battle.Enemies[2].Creature.CurrentHp;
        Assert(follower1 > 0 && follower2 > 0 && priest > 0, "root has three living enemies");
        Assert(!Kernel.IsPrimaryEnemy(battle, Kernel.EnemyOwner(0))
            && !Kernel.IsPrimaryEnemy(battle, Kernel.EnemyOwner(1)),
            "both KinFollower minions are secondary");
        Assert(Kernel.IsPrimaryEnemy(battle, Kernel.EnemyOwner(2)), "KinPriest is primary");
        Assert(Remaining(battle, item.Rules, scorer) == priest,
            "minion current HP is not part of the remaining-to-kill cap");

        Battle priestDead = item.Battles[0];
        priestDead.Enemies[2].Creature.CurrentHp = 0;
        priestDead.Enemies[2].DeathPhase = DeathPhase.PermanentlyDead;
        Assert(Remaining(priestDead, item.Rules, scorer) == 0,
            "the last primary enemy's death ends combat without killing the minions");
    }

    private static void CheckRevivingIllusionMinion()
    {
        (SearchCase item, Battle battle, Scorer scorer) = Load("km-fogmog-normal");
        Assert(battle.EnemyCount == 1, "Fogmog root has one primary enemy");
        int fogmog = battle.Enemies[0].Creature.CurrentHp;
        Assert(fogmog > 0, "Fogmog starts alive");
        Assert(Remaining(battle, item.Rules, scorer) == fogmog, "root cap is Fogmog's current HP");

        if (!Registry.TryMonster("EYE_WITH_TEETH", out ushort eye))
            throw new InvalidDataException("remaining-enemy-hp: missing EYE_WITH_TEETH");
        if (!Registry.TryPower("ILLUSION_POWER", out ushort illusionPower))
            throw new InvalidDataException("remaining-enemy-hp: missing ILLUSION_POWER");
        if (!item.Rules.TrySpawnRulesId(eye, out int rulesId))
            throw new InvalidDataException("remaining-enemy-hp: no compiled EyeWithTeeth rules");
        Exec x = new(ref battle, item.Rules, default);
        byte illusionOwner = Kernel.SpawnMonster(ref x, Kernel.EnemyOwner(0), rulesId,
            Kernel.NoSlot, minion: true);
        Assert(!x.NeedsChoice, "spawning EyeWithTeeth needs no choice");
        Assert(!Kernel.IsPrimaryEnemy(battle, illusionOwner),
            "EyeWithTeeth is secondary through MinionPower");
        Assert(Remaining(battle, item.Rules, scorer) == fogmog,
            "a summoned illusion's current HP is not part of the cap");

        ref Enemy illusion = ref battle.Enemies[illusionOwner - Limits.FirstEnemyOwner];
        Assert(illusion.Creature.CurrentHp > 0 && illusion.Creature.MaxHp > 0,
            "the summoned illusion has positive HP");
        illusion.Creature.CurrentHp = 0;
        illusion.DeathPhase = DeathPhase.Reviving;
        Assert(Kernel.GetAmount(battle, illusionOwner, illusionPower) > 0,
            "EyeWithTeeth keeps IllusionPower while reviving");
        Assert(Remaining(battle, item.Rules, scorer) == fogmog,
            "an illusion's revive HP is not part of the cap");
    }
}
