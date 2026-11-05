using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>ThieveryPower</c> (GremlinMerc, one instanced stack of 20 applied in
/// <c>AfterAddedToRoom</c>, targeting the player). Its counter is the steal cap per GIMME-family move;
/// <see cref="Power.Aux0"/> is the native <c>DynamicVars.Gold</c> ledger accumulated by
/// <c>SimulatedCombatState.RecordThievery</c>. On death, <c>SurprisePower</c> transfers Aux0 into a
/// <c>HeistPower</c> on the replacement fat gremlin.
/// </summary>
[ContentId("THIEVERY_POWER")]
public sealed class ThieveryPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    // Native PowerInstanceType.Instanced: every application is its own row.
    public override bool IsInstanced => true;
}

/// <summary>
/// Native <c>HeistPower</c>: instanced gold carried by the fat gremlin that replaced the merc. Its
/// amount is the stolen gold; killing the carrier returns it
/// (<c>SimulatedCombatState.RecoverStolenResources</c>). <c>BeforeDeath</c> only adds a run reward and
/// records the map history, neither of which is prediction state.
/// </summary>
[ContentId("HEIST_POWER")]
public sealed class HeistPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsInstanced => true;
}

/// <summary>
/// Native <c>SwipePower</c>: one instanced row per card stolen by THIEVERY. The complete stolen-card
/// value lives in <see cref="Battle.DetachedCards"/> and <see cref="Power.Aux0"/> is its
/// slot + 1; removing the power clears the slot. Killing the owner recovers the card through
/// <c>RecoverStolenResources</c>; escaping removes the power without returning it.
/// </summary>
[ContentId("SWIPE_POWER")]
public sealed class SwipePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool IsInstanced => true;

    public override bool UsesDetachedCard => true;
}

/// <summary>
/// Native <c>SurprisePower</c> (GremlinMerc, one stack). While positive it stops the combat from
/// ending, and its deferred death pass creates the fat gremlin (with the merc's Thievery gold
/// transferred into Heist rows) and a sneaky gremlin. The native order is preserved:
/// <c>CreateCreature(fat)</c> (CombatId and Niche HP draw first) → add sneaky → add fat.
/// </summary>
[ContentId("SURPRISE_POWER")]
public sealed class SurprisePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool PreventsCombatEnding => true;

    public override bool SpawnsPrimaryEnemyOnDeath => true;

    public override void DeathTrigger(ref Exec x, Listener self, byte dead)
    {
        if (self.Owner != dead)
            return;
        // DeathPowerSupport.Trigger: MonsterSpawnSupport.Create<FatGremlin>(..., "fat") first.
        int fatRulesId = x.R.SpawnRulesId(Registry.MonsterId<Monsters.Act1.FatGremlin>());
        Enemy fat = default;
        Kernel.CreateMonster(ref x, fatRulesId, Kernel.SlotIndex(x.R, "fat"), int.MinValue, ref fat);
        // Stolen gold is read from the still-present Thievery rows before they are cleaned up.
        Span<int> gold = stackalloc int[Limits.Powers];
        Span<byte> targets = stackalloc byte[Limits.Powers];
        int transfers = 0;
        ushort thievery = Registry.PowerId<ThieveryPower>();
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref readonly Power power = ref x.B.Powers[index];
            if (power.Id != thievery || power.Owner != dead || power.Amount <= 0)
                continue;
            gold[transfers] = power.Aux0;
            targets[transfers] = power.Target;
            transfers++;
        }
        // MonsterSpawnSupport.Spawn<SneakyGremlin>(simulator, combat, dead, "sneaky"): the sneaky
        // gremlin is added before the fat one, so its CombatId follows the fat create and its HP draw
        // excludes only the creatures already in the roster.
        int sneakyRulesId = x.R.SpawnRulesId(Registry.MonsterId<Monsters.Act1.SneakyGremlin>());
        Kernel.SpawnMonster(ref x, dead, sneakyRulesId, Kernel.SlotIndex(x.R, "sneaky"), minion: false);
        if (x.NeedsChoice)
            return;
        byte fatOwner = Kernel.AddCreatedMonster(ref x, dead, ref fat, minion: false);
        if (x.NeedsChoice)
            return;
        ushort heist = Registry.PowerId<HeistPower>();
        for (int index = 0; index < transfers; index++)
        {
            if (gold[index] == 0)
                continue;
            Kernel.ApplyPowerTargeted(ref x, fatOwner, targets[index], heist, gold[index], dead);
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// Native <c>SteamEruptionPower</c> (Waterfall Giant): a counter that stops the combat from ending,
/// keeps the dead owner in the roster and rewrites its death into the forced about-to-blow phase
/// (<c>SteamEruptionPower.AfterDeath</c> → <c>WaterfallGiant.TriggerAboutToBlowState</c>). The power
/// survives the owner's death cleanup (<c>ShouldPowerBeRemovedAfterOwnerDeath</c>).
/// </summary>
[ContentId("STEAM_ERUPTION_POWER")]
public sealed class SteamEruptionPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool PreventsRemovalAfterDeath => true;

    public override bool PreventsCombatEnding => true;

    public override bool ShouldPowerBeRemovedAfterOwnerDeath => false;

    public override void AfterDeath(ref Exec x, Listener self, byte creature, bool wasRemovalPrevented)
    {
        if (wasRemovalPrevented || creature != self.Owner)
            return;
        Kernel.TriggerSteamEruptionDeath(ref x, self.Owner);
    }
}

/// <summary>
/// Native <c>EscapeArtistPower</c> (ThievingHopper, 5 stacks applied in <c>AfterAddedToRoom</c>): a
/// visual countdown. At the end of the enemy side it decrements while above 1
/// (<c>EndTurnPowerSupport.TriggerRegular</c> case).
/// </summary>
[ContentId("ESCAPE_ARTIST_POWER")]
public sealed class EscapeArtistPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (side != Side.Enemy || !participates)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 1)
            Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, amount - 1);
    }
}

/// <summary>
/// Native <c>FlutterPower</c> (ThievingHopper, 5 stacks from FLUTTER_MOVE): powered attacks against
/// the owner deal <c>DamageDecrease</c>% damage; each unblocked powered hit removes one stack, and
/// the last stack stuns the owner with the next state of its most recently logged move
/// (<c>AfterDamageReceivedMirrors.HandleFlutterPower</c>).
/// </summary>
[ContentId("FLUTTER_POWER")]
public sealed class FlutterPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override IReadOnlyList<string> VarNames => ["DamageDecrease"];

    // The existing engine decrements Flutter through a PowerAmountPredictionState; when the hopper
    // dies before the next synchronization, its death cleanup zeroes the model but the pending amount
    // is restored afterwards, leaving the row on the removed enemy. Sts2Solver has no such lag, so keep the
    // row explicitly. It is inert once the owner leaves the roster, and no other monster has Flutter.
    public override bool ShouldPowerBeRemovedAfterOwnerDeath => false;

    public override bool PowerRemainsAfterOwnerLeavesRoster => true;

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self,
        in DamageContext damage, decimal amount)
    {
        if (damage.Target != self.Owner || !damage.Props.IsPoweredAttack())
            return 1m;
        return x.R.PowerVar(Id, 0) / 100m;
    }

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result,
        byte dealer)
    {
        if (result.Receiver != self.Owner || result.UnblockedDamage == 0 || !result.Props.IsPoweredAttack())
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, amount - 1);
        // PowerCmd.Decrement is a no-op while IsEnding; only the live amount reaching zero stuns.
        if (Kernel.GetAmount(x.B, self.Owner, Id) > 0)
            return;
        Kernel.ForceStunnedAfterLastMove(ref x, self.Owner);
    }
}
