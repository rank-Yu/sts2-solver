using System.Text.Json;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;

namespace Sts2Solver;

/// <summary>
/// Pure settings/localization checks. They use a temporary directory and never touch the live
/// settings file, so the headless request suite starts from the same defaults.
/// </summary>
internal static class UiLogicChecks
{
    internal static UiLogicCheckResult Run(string testRoot)
    {
        string directory = Path.Combine(testRoot, "ui-checks-tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        List<UiLogicCheck> checks = [];
        int passed = 0;
        int failed = 0;
        void Check(string name, Action body)
        {
            try
            {
                body();
                passed++;
                checks.Add(new UiLogicCheck(name, "ok", null));
            }
            catch (InvalidDataException exception)
            {
                failed++;
                checks.Add(new UiLogicCheck(name, "failed", exception.Message));
            }
            catch (JsonException exception)
            {
                failed++;
                checks.Add(new UiLogicCheck(name, "failed", "json:" + exception.Message));
            }
            catch (IOException exception)
            {
                failed++;
                checks.Add(new UiLogicCheck(name, "failed", "io:" + exception.Message));
            }
            catch (UnauthorizedAccessException exception)
            {
                failed++;
                checks.Add(new UiLogicCheck(name, "failed", "access:" + exception.Message));
            }
            catch (NotSupportedException exception)
            {
                failed++;
                checks.Add(new UiLogicCheck(name, "failed", "unsupported:" + exception.Message));
            }
            catch (InvalidOperationException exception)
            {
                failed++;
                checks.Add(new UiLogicCheck(name, "failed", "invalid_operation:" + exception.Message));
            }
        }

        Check("jit_prepare_methods", JitChecks.Run);
        Check("settings_defaults", Defaults);
        Check("settings_round_trip", () => RoundTrip(directory));
        Check("settings_corrupt_fallback", () => CorruptFallback(directory));
        Check("settings_third_party_current", () => ThirdPartyCurrentSchema(directory));
        Check("settings_schema6_upgrade", () => Schema6Upgrade(directory));
        Check("settings_forced_potion_upgrade", () => ForcedPotionUpgrade(directory));
        Check("potion_directive_reconcile", PotionDirectiveReconcile);
        Check("deployment_timing", DeploymentTiming);
        Check("full_auto_pause_policy", PausePolicy);
        Check("search_memory_priority", MemoryPriority);
        Check("search_memory_default", MemoryDefault);
        Check("settings_untouched_null", UntouchedNull);
        Check("search_thread_count", ThreadCount);
        Check("result_summary_speed", ResultSummarySpeed);
        Check("english_catalog_placeholders", LocaleCatalog);
        Check("panel_text_catalog", PanelTextCatalog);
        Check("failure_reason_text", FailureReasonText);
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // The test result is already recorded; a leftover temporary directory is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
        UiLogicCheckResult result = new(1, failed == 0 ? "ok" : "failed", passed, failed, [.. checks]);
        ModLog.Info(
            $"[Sts2Solver] UI_LOGIC_CHECKS status={result.Status} passed={passed} failed={failed}");
        foreach (UiLogicCheck check in checks.Where(static check => check.Status != "ok"))
            ModLog.Error($"[Sts2Solver] UI_LOGIC_CHECK_FAILED name={check.Name} reason={check.Error}");
        return result;
    }

    private static void Defaults()
    {
        SolverSettingsData data = new();
        data.Validate();
        Require(data.SchemaVersion == SolverSettingsData.CurrentSchemaVersion, "schema_version");
        Require(data.Search.Tier == SearchPerformanceTier.Medium, "tier_default");
        Require(data.Search.MemoryBudgetBytes is null, "memory_override_default");
        Require(data.Search.Threads is null, "threads_default");
        Require(data.Search.Potions.DefaultMode == PotionUseMode.Smart, "potion_mode_default");
        Require(data.Search.Potions.Preset == SolverPotionPreset.Smart, "potion_preset_default");
        Require(data.Search.Potions.Directives.Length == 0, "potion_directives_default");
        Require(data.Search.GrowthBudgets.ToPolicy() == default, "growth_default");
        Require(data.Search.GrowthBudgets.ThirdParty.Count == 0, "growth_third_party_default");
        Require(!data.Search.IgnoreLongTermRewards, "ignore_long_term_default");
        Require(data.Search.BrightestFlameMaxHpLossLimit is null, "brightest_flame_default");
        bool flameRejected = false;
        try
        {
            (new SolverSettingsData
            {
                Search = new SolverSearchSettings { BrightestFlameMaxHpLossLimit = 1001 },
            }).Validate();
        }
        catch (InvalidDataException)
        {
            flameRejected = true;
        }
        Require(flameRejected, "brightest_flame_out_of_range");
        Require(data.Search.ActTransitionBossHpStrategy == SearchBossHpStrategy.ProgressionFirst,
            "act_boss_default");
        Require(data.Search.FinalBossHpStrategy == SearchBossHpStrategy.ProgressionFirst,
            "final_boss_default");
        Require(data.Automation.AutoSearch, "auto_search_default");
        Require(data.Automation.DeploymentSpeed == SolverDeploymentSpeed.FollowGame,
            "deployment_speed_default");
        Require(data.Automation.InterActionDelaySeconds == 0d, "inter_action_delay_default");
        Require(data.Ui.ShowBattleDamagePerformanceHint, "performance_hint_default");
        Require(data.Ui.PanelX is null && data.Ui.PanelY is null, "panel_position_default");
        Require(data.Ui.PanelWidth is null && data.Ui.PanelHeight is null, "panel_size_default");
        Require(!data.Ui.Collapsed, "collapsed_default");
        Require(Math.Abs(data.Ui.Opacity - 0.65f) < 0.0001f, "opacity_default");
        Require(data.Ui.Theme == SolverUiTheme.Dark, "theme_default");
    }

    private static void RoundTrip(string directory)
    {
        string path = Path.Combine(directory, "settings.json");
        List<string> logs = [];
        SolverSettingsStore store = SolverSettingsStore.Load(path, logs.Add, logs.Add);
        SolverSettingsData changed = new()
        {
            Search = new SolverSearchSettings
            {
                Tier = SearchPerformanceTier.High,
                MemoryBudgetBytes = 2L << 30,
                Threads = 3,
                Potions = new SolverPotionSettings
                {
                    DefaultMode = PotionUseMode.Disabled,
                    Preset = SolverPotionPreset.ProtectAll,
                    Directives = [new PotionSlotDirective(0, "FIRE_POTION", PotionDirective.Disabled)],
                },
                GrowthBudgets = new SolverGrowthBudgets
                {
                    HandOfGreed = 5,
                    Feed = 3,
                    ThirdParty = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["example_mod:growth"] = JsonSerializer.SerializeToElement(7),
                    },
                },
                IgnoreLongTermRewards = true,
                BrightestFlameMaxHpLossLimit = 3,
                ActTransitionBossHpStrategy = SearchBossHpStrategy.MinimizeHpLoss,
                FinalBossHpStrategy = SearchBossHpStrategy.MinimizeHpLoss,
            },
            Automation = new SolverAutomationSettings
            {
                AutoSearch = false,
                DeploymentSpeed = SolverDeploymentSpeed.Fast,
                InterActionDelaySeconds = 0.5,
            },
            Ui = new SolverUiSettings
            {
                PanelX = 12.5f,
                PanelY = 24.5f,
                PanelWidth = 800f,
                PanelHeight = 600f,
                Collapsed = true,
                Opacity = 0.8f,
                Theme = SolverUiTheme.Light,
            },
        };
        bool updated = store.Update(_ => changed, out string? error);
        Require(updated, "update_failed:" + error);
        Require(File.Exists(path), "file_missing");
        Require(!File.Exists(path + ".tmp"), "temp_file_left");
        SolverSettingsStore loaded = SolverSettingsStore.Load(path, logs.Add, logs.Add);
        string expected = JsonSerializer.Serialize(changed, SolverSettingsStore.JsonOptions);
        string actual = JsonSerializer.Serialize(loaded.Current, SolverSettingsStore.JsonOptions);
        Require(string.Equals(expected, actual, StringComparison.Ordinal), "round_trip_mismatch");
        Require(loaded.Current.Search.GrowthBudgets.ThirdParty.ContainsKey("example_mod:growth"),
            "round_trip_third_party_growth");
        Require(!actual.Contains("\"theft\"", StringComparison.Ordinal), "round_trip_theft_field");
        Require(logs.Any(static line => line.Contains("SETTINGS_SAVED", StringComparison.Ordinal)),
            "save_log_missing");
    }

    private static void CorruptFallback(string directory)
    {
        string path = Path.Combine(directory, "corrupt.json");
        File.WriteAllText(path, "{ this is not json");
        List<string> logs = [];
        SolverSettingsStore store = SolverSettingsStore.Load(path, logs.Add, logs.Add);
        Require(store.Current.Search.Tier == SearchPerformanceTier.Medium, "corrupt_not_default");
        Require(Directory.GetFiles(directory, "corrupt.bad-*.json").Length == 1, "backup_missing");
        Require(logs.Any(static line => line.Contains("SETTINGS_CORRUPT", StringComparison.Ordinal)),
            "corrupt_log_missing");

        string invalidPath = Path.Combine(directory, "invalid.json");
        File.WriteAllText(invalidPath,
            "{\"schemaVersion\":1,\"ui\":{\"opacity\":0.1}}");
        List<string> invalidLogs = [];
        SolverSettingsStore invalid = SolverSettingsStore.Load(invalidPath, invalidLogs.Add,
            invalidLogs.Add);
        Require(invalid.Current.Ui.Opacity > 0.24f, "invalid_not_default");
        Require(Directory.GetFiles(directory, "invalid.bad-*.json").Length == 1,
            "invalid_backup_missing");
        Require(invalidLogs.Any(static line =>
                line.Contains("SETTINGS_CORRUPT", StringComparison.Ordinal)),
            "invalid_log_missing");
    }

    private static void ThirdPartyCurrentSchema(string directory)
    {
        string path = Path.Combine(directory, "current-third-party.json");
        File.WriteAllText(path,
            "{\"schemaVersion\":8,\"search\":{\"growthBudgets\":{" +
            "\"handOfGreed\":4,\"example_mod:growth\":7}}}");
        List<string> logs = [];
        SolverSettingsStore loaded = SolverSettingsStore.Load(path, logs.Add, logs.Add);
        Require(loaded.Current.SchemaVersion == SolverSettingsData.CurrentSchemaVersion,
            "third_party_schema");
        Require(loaded.Current.Search.GrowthBudgets.HandOfGreed == 4,
            "third_party_growth");
        Require(loaded.Current.Search.GrowthBudgets.ThirdParty.TryGetValue(
            "example_mod:growth", out System.Text.Json.JsonElement thirdParty)
            && thirdParty.GetInt32() == 7, "third_party_value");
        Require(!Directory.GetFiles(directory, "current-third-party.bad-*.json").Any(),
            "third_party_corrupt");
    }

    /// <summary>
    /// Schema 6 stored the three-position mode. Loading it keeps every other value and maps the mode
    /// onto the auto-search switch: Manual off, AutoSearch and FullAuto on.
    /// </summary>
    private static void Schema6Upgrade(string directory)
    {
        foreach ((string mode, bool expected) in new[] { ("Manual", false), ("AutoSearch", true), ("FullAuto", true) })
        {
            string path = Path.Combine(directory, $"schema6-{mode}.json");
            File.WriteAllText(path,
                "{\"schemaVersion\":6,\"automation\":{\"mode\":\"" + mode + "\",\"deploymentSpeed\":\"Fast\"}," +
                "\"ui\":{\"opacity\":0.8}}");
            List<string> logs = [];
            SolverSettingsStore loaded = SolverSettingsStore.Load(path, logs.Add, logs.Add);
            Require(loaded.Current.SchemaVersion == SolverSettingsData.CurrentSchemaVersion,
                $"schema6_{mode}_schema");
            Require(loaded.Current.Automation.AutoSearch == expected, $"schema6_{mode}_auto_search");
            Require(loaded.Current.Automation.DeploymentSpeed == SolverDeploymentSpeed.Fast,
                $"schema6_{mode}_speed_kept");
            Require(Math.Abs(loaded.Current.Ui.Opacity - 0.8f) < 0.0001f, $"schema6_{mode}_opacity_kept");
            Require(!Directory.GetFiles(directory, $"schema6-{mode}.bad-*.json").Any(),
                $"schema6_{mode}_not_corrupt");
            Require(logs.Any(static line => line.Contains("SETTINGS_UPGRADED from=6", StringComparison.Ordinal)),
                $"schema6_{mode}_log");
        }
    }

    /// <summary>
    /// Schemas 6 and 7 could force a potion. Loading drops those directives, keeps the others and
    /// turns the force-all preset into Smart.
    /// </summary>
    private static void ForcedPotionUpgrade(string directory)
    {
        foreach (int schema in new[] { 6, 7 })
        {
            string path = Path.Combine(directory, $"schema{schema}-forced.json");
            File.WriteAllText(path,
                "{\"schemaVersion\":" + schema + ",\"search\":{\"potions\":{\"preset\":\"ForceAll\"," +
                "\"directives\":[{\"slot\":0,\"potionId\":\"FIRE_POTION\",\"directive\":\"Force\"}," +
                "{\"slot\":1,\"potionId\":\"BLOCK_POTION\",\"directive\":\"Disabled\"}]}}}");
            List<string> logs = [];
            SolverSettingsStore loaded = SolverSettingsStore.Load(path, logs.Add, logs.Add);
            SolverPotionSettings potions = loaded.Current.Search.Potions;
            Require(loaded.Current.SchemaVersion == SolverSettingsData.CurrentSchemaVersion,
                $"schema{schema}_forced_schema");
            Require(potions.Preset == SolverPotionPreset.Smart, $"schema{schema}_forced_preset");
            Require(potions.Directives.Length == 1 && potions.Directives[0].Slot == 1
                && potions.Directives[0].Directive == PotionDirective.Disabled,
                $"schema{schema}_forced_directives");
            Require(!Directory.GetFiles(directory, $"schema{schema}-forced.bad-*.json").Any(),
                $"schema{schema}_forced_not_corrupt");
            Require(logs.Any(line => line.Contains($"SETTINGS_UPGRADED from={schema} to=8",
                    StringComparison.Ordinal)), $"schema{schema}_forced_log");
        }
    }

    private static void PotionDirectiveReconcile()
    {
        SearchPotionDirective[] persisted =
        [
            new(0, "FIRE_POTION", SearchPotionMode.Disabled),
            new(1, "BLOCK_POTION", SearchPotionMode.Disabled),
            new(2, "STRENGTH_POTION", SearchPotionMode.Disabled),
        ];
        IReadOnlyList<SearchPotionDirective> reconciled = SearchPolicyCapture.ReconcilePotionDirectives(
            persisted,
            [(0, "FIRE_POTION"), (1, "STRENGTH_POTION")]);
        Require(reconciled.Count == 1, "reconcile_count");
        Require(reconciled[0].Slot == 0 && reconciled[0].PotionId == "FIRE_POTION"
            && reconciled[0].Mode == SearchPotionMode.Disabled, "reconcile_match");
        IReadOnlyList<SearchPotionDirective> replaced = SearchPolicyCapture.ReconcilePotionDirectives(
            persisted,
            [(0, "BLOCK_POTION")]);
        Require(replaced.Count == 0, "reconcile_slot_replaced");
        IReadOnlyList<SearchPotionDirective> empty = SearchPolicyCapture.ReconcilePotionDirectives(
            persisted, []);
        Require(empty.Count == 0, "reconcile_empty_belt");

        PotionSlotDirective[] settingsDirectives =
        [
            new(0, "FIRE_POTION", PotionDirective.Disabled),
            new(1, "BLOCK_POTION", PotionDirective.Disabled),
            new(2, "STRENGTH_POTION", PotionDirective.Disabled),
        ];
        PotionSlotDirective[] settingsRetained = SolverPotionDirectives.Reconcile(
            settingsDirectives, [(0, "FIRE_POTION"), (1, "STRENGTH_POTION")]);
        Require(settingsRetained.Length == 1
            && settingsRetained[0].Slot == 0
            && settingsRetained[0].PotionId == "FIRE_POTION", "settings_reconcile_match");
        Require(SolverPotionDirectives.Reconcile(settingsDirectives, [(0, "BLOCK_POTION")]).Length == 0,
            "settings_reconcile_replaced");
        Require(SolverPotionDirectives.Reconcile(settingsDirectives, []).Length == 0,
            "settings_reconcile_empty");
        SolverPotionSettings disabled = new SolverPotionSettings()
            .ApplyDirective(0, "FIRE_POTION", SolverPotionMode.Disabled);
        Require(disabled.Directives.Length == 1
            && disabled.Directives[0].Directive == PotionDirective.Disabled,
            "disabled_directive_persisted");
        Require(disabled.ApplyDirective(0, "FIRE_POTION", SolverPotionMode.Smart)
            .Directives.Length == 0, "smart_directive_removed");
        Require(disabled.ResolveDirective(0, "FIRE_POTION") == PotionDirective.Disabled,
            "disabled_directive_resolved");
    }

    private static void DeploymentTiming()
    {
        Require(CombatDeploymentTiming.ResolveFastMode(SolverDeploymentSpeed.FollowGame) is null,
            "speed_follow_game");
        Require(CombatDeploymentTiming.ResolveFastMode(SolverDeploymentSpeed.Normal)
            == MegaCrit.Sts2.Core.Settings.FastModeType.Normal, "speed_normal");
        Require(CombatDeploymentTiming.ResolveFastMode(SolverDeploymentSpeed.Fast)
            == MegaCrit.Sts2.Core.Settings.FastModeType.Fast, "speed_fast");
        SolverAutomationSettings settings = new();
        Require(settings.DeploymentSpeed == SolverDeploymentSpeed.FollowGame, "speed_default");
        Require(settings.InterActionDelaySeconds == 0d, "delay_default");
        SolverAutomationSettings valid = settings with { DeploymentSpeed = SolverDeploymentSpeed.Fast,
            InterActionDelaySeconds = 3d };
        (new SolverSettingsData { Automation = valid }).Validate();
        SolverAutomationSettings invalid = settings with { InterActionDelaySeconds = 3.01 };
        bool rejected = false;
        try
        {
            (new SolverSettingsData { Automation = invalid }).Validate();
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        Require(rejected, "delay_out_of_range");
    }

    private static void PausePolicy()
    {
        CombatRouteOutcome death = new(3, null, 20, 0, false, false,
            new Dictionary<int, int> { [3] = 5 });
        Require(CombatFullAutoPausePolicy.BeforeDeployment(3, death, noSurvivalPause: true)
            == CombatPauseCode.DeathTurn, "death_pause");
        Require(CombatFullAutoPausePolicy.BeforeDeployment(3, death, noSurvivalPause: false)
            == CombatPauseCode.DeathTurn, "death_pause_after_resume");
        Require(CombatFullAutoPausePolicy.BeforeDeployment(2, death, noSurvivalPause: true)
            == CombatPauseCode.NoSurvival, "no_survival_pause");
        Require(CombatFullAutoPausePolicy.BeforeDeployment(2, death, noSurvivalPause: false) == null,
            "no_survival_once_per_combat");
        Require(CombatFullAutoPausePolicy.Code(CombatPauseCode.NoSurvival) == "no_survival",
            "no_survival_code");
        CombatRouteOutcome end = new(null, 3, 20, 0, true, false,
            new Dictionary<int, int> { [3] = 5 });
        Require(CombatFullAutoPausePolicy.BeforeDeployment(3, end, noSurvivalPause: true) == null,
            "combat_end_never_pauses");
        CombatRouteOutcome open = new(null, null, 20, 0, false, false,
            new Dictionary<int, int> { [3] = 5 });
        Require(CombatFullAutoPausePolicy.BeforeDeployment(3, open, noSurvivalPause: true) == null,
            "open_route_never_pauses");
        Require(CombatFullAutoPausePolicy.LiveEndTurn(5, 5, false) == null, "live_equal");
        Require(CombatFullAutoPausePolicy.LiveEndTurn(5, 6, false)
            == CombatPauseCode.LiveEndTurnRisk, "live_worse");
        Require(CombatFullAutoPausePolicy.LiveEndTurn(5, 4, false) == null, "live_better");
        Require(CombatFullAutoPausePolicy.LiveEndTurn(5, 5, true)
            == CombatPauseCode.LiveEndTurnRisk, "live_death");
        // Burning Blood 6 + Meat on the Bone 12 at or below 50%: the panel and route ranking both count
        // the heal, clamped to the missing HP. Ending on the threshold pays off from 41 to 51 HP.
        Sts2Solver.Ai.Combat.Policy.PostCombatRelicHealProfile heal = new(6, 12, 50);
        Require(heal.HealFor(70, 80) == 6, "relic_heal_unconditional");
        Require(heal.HealFor(40, 80) == 18, "relic_heal_threshold");
        Require(heal.HealFor(78, 80) == 2, "relic_heal_clamped");
        Require(Sts2Solver.Ai.Combat.Policy.ActEndingBossPolicy.RankedPostCombatRelicHeal(heal, true, 40, 80) == 18,
            "relic_heal_ranking_threshold");
        Require(heal.ThresholdHealPaysOff(45, 80), "relic_heal_threshold_pays");
        Require(!heal.ThresholdHealPaysOff(52, 80) && heal.ThresholdHealPaysOff(51, 80),
            "relic_heal_threshold_break_even");
        Require(!heal.ThresholdHealPaysOff(40, 80), "relic_heal_on_threshold");
    }

    private static void MemoryPriority()
    {
        const long gib = 1L << 30;
        SearchMemoryBudgetResolution settings = SearchMemoryBudget.Resolve(2 * gib, gib);
        Require(settings.Source == SearchMemoryBudgetSource.Settings, "settings_source");
        Require(settings.Bytes == 2 * gib, "settings_bytes");
        SearchMemoryBudgetResolution fallback = SearchMemoryBudget.Resolve(null, gib);
        Require(fallback.Source == SearchMemoryBudgetSource.Default, "default_source");
        Require(fallback.Bytes == gib, "default_bytes");
        SearchMemoryBudgetResolution invalid = SearchMemoryBudget.Resolve(0, gib);
        Require(invalid.Source == SearchMemoryBudgetSource.Default, "invalid_setting_fallthrough");
        Require(invalid.Bytes == gib, "invalid_setting_bytes");
    }

    private static void MemoryDefault()
    {
        const long gib = 1L << 30;
        Require(SearchMemoryBudget.DefaultBytes(16 * gib) == 3_435_973_836L, "twenty_percent");
        Require(SearchMemoryBudget.DefaultBytes(4 * gib) == gib, "floor");
        Require(SearchMemoryBudget.DefaultBytes(0) == gib, "unknown_available");
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        Require(SearchMemoryBudget.DefaultBytes() == SearchMemoryBudget.DefaultBytes(available),
            "runtime_available");
    }

    private static void UntouchedNull()
    {
        SolverSettingsData data = new();
        Require(data.Search.Threads is null && data.Search.MemoryBudgetBytes is null, "defaults_null");
        Require(SolverSettingsPage.TryParseThreads("6", 6, out int? same) && same is null,
            "threads_default_null");
        Require(SolverSettingsPage.TryParseThreads(" ", 6, out int? empty) && empty is null,
            "threads_empty_null");
        Require(SolverSettingsPage.TryParseThreads("4", 6, out int? four) && four == 4, "threads_value");
        Require(!SolverSettingsPage.TryParseThreads("9", 6, out _)
            && !SolverSettingsPage.TryParseThreads("0", 6, out _)
            && !SolverSettingsPage.TryParseThreads("2.5", 6, out _), "threads_invalid");
        const long defaultBytes = 5_100_000_000L;
        const long maximumBytes = 32_000_000_000L;
        Require(SolverSettingsPage.TryParseMemoryGigabytes(
                SolverSettingsPage.FormatGigabytes(defaultBytes), defaultBytes, maximumBytes,
                out long? sameMemory)
            && sameMemory is null, "memory_default_null");
        Require(SolverSettingsPage.TryParseMemoryGigabytes(string.Empty, defaultBytes, maximumBytes,
                out long? emptyMemory)
            && emptyMemory is null, "memory_empty_null");
        Require(SolverSettingsPage.TryParseMemoryGigabytes("8", defaultBytes, maximumBytes, out long? eight)
            && eight == 8_000_000_000L, "memory_value");
        Require(SolverSettingsPage.TryParseMemoryGigabytes("6.25", defaultBytes, maximumBytes,
                out long? rounded)
            && rounded == 6_300_000_000L, "memory_tenth");
        Require(!SolverSettingsPage.TryParseMemoryGigabytes("0.1", defaultBytes, maximumBytes, out _)
            && !SolverSettingsPage.TryParseMemoryGigabytes("40", defaultBytes, maximumBytes, out _)
            && !SolverSettingsPage.TryParseMemoryGigabytes("abc", defaultBytes, maximumBytes, out _),
            "memory_invalid");
    }

    private static void ThreadCount()
    {
        SearchThreadCountResolution many = SearchThreadCount.Resolve(24);
        Require(many.Threads == 8 && many.Source == SearchThreadCountSource.Default,
            "default_upper_clamp");
        SearchThreadCountResolution four = SearchThreadCount.Resolve(4);
        Require(four.Threads == 2, "default_minus_two");
        SearchThreadCountResolution one = SearchThreadCount.Resolve(1);
        Require(one.Threads == 1, "default_floor");
        SearchThreadCountResolution settings = SearchThreadCount.Resolve(24, 3);
        Require(settings.Threads == 3 && settings.Source == SearchThreadCountSource.Settings,
            "settings_source");
        SearchThreadCountResolution settingsClamped = SearchThreadCount.Resolve(24, 99);
        Require(settingsClamped.Threads == 8 && settingsClamped.Source == SearchThreadCountSource.Settings,
            "settings_clamp");
        SearchThreadCountResolution settingsBeatsDefault = SearchThreadCount.Resolve(4, 3);
        Require(settingsBeatsDefault.Threads == 3
            && settingsBeatsDefault.Source == SearchThreadCountSource.Settings,
            "settings_beats_default");
    }

    private static void ResultSummarySpeed()
    {
        string review = SolverOverlay.FormatSearchReview(1000, 100, 1000, reused: false);
        Require(review.Contains("（100 条/s）", StringComparison.Ordinal), review);
        Require(!review.Contains("1,000 条/s", StringComparison.Ordinal), review);
        Require(SolverOverlay.FormatResultSummary(2, endsThisTurn: true)
            .Contains("本回合结束战斗", StringComparison.Ordinal), "combat_end_summary");
        Require(SolverOverlay.FormatDeathSummary(5, 3)
            .Contains("预计第 5 回合死亡", StringComparison.Ordinal), "death_summary_later");
        Require(SolverOverlay.FormatDeathSummary(3, 3)
            .Contains("预计本回合死亡", StringComparison.Ordinal), "death_summary_this_turn");
    }

    private static void LocaleCatalog()
    {
        string[] problems = SolverText.ValidateEnglishCatalog();
        Require(problems.Length == 0, "catalog:" + string.Join(",", problems));
        Require(SolverText.EnglishCatalogForTesting.Count > 0, "catalog_empty");
        Require(SolverText.EnglishCatalogForTesting.ContainsKey("尖塔求解器"), "catalog_title_key");
    }

    private static void FailureReasonText()
    {
        int cardLimit = Sts2Solver.Simulation.Combat.Limits.Cards;
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.ImportCapacity,
            Region = "Cards",
            Capacity = cardLimit,
            Actual = cardLimit + 1,
        }) == SolverText.Format($"牌组共 {cardLimit + 1} 张，超过求解器上限 {cardLimit} 张"),
            "deck_capacity");
        Require(SolverPanelText.CapacityDropHint(
                new Dictionary<string, int>(StringComparer.Ordinal) { ["Cards"] = 1 })
            == "部分打法因牌数超出求解器容量被跳过，可能还有更好的路线",
            "capacity_drop_hint_cards");
        Require(SolverPanelText.CapacityDropHint(
                new Dictionary<string, int>(StringComparer.Ordinal) { ["Powers"] = 1 })
            .Contains("能力", StringComparison.Ordinal),
            "capacity_drop_hint_region");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.SearchCapacityBounded,
        }).Contains("生成的牌太多", StringComparison.Ordinal), "capacity_bounded");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.SearchNoRoute,
        }) == "没有找到任何可执行路线", "no_route");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.SearchMemoryLimit,
        }).Contains("内存上限", StringComparison.Ordinal), "memory_limit");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.Import,
            Code = ImportReason.UnsupportedCard,
            Detail = "NO_SUCH_CARD",
        }) == "战斗中有尚未支持的卡牌", "unsupported_card_category");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.Deployment,
            Code = "action_failed",
        }) == "原生动作执行出错", "deployment_action_failed");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.Deployment,
            Code = "boundary_timeout",
        }) == "等待游戏结算超时", "deployment_timeout");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.Deviation,
            Code = "energy_drift",
        }) == "能量和路线对不上", "deviation_energy");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.Deployment,
            Code = "not_a_known_reason",
        }) == "详细原因已写入日志", "deployment_unknown");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.Deviation,
            Diff = ["HandCount"],
        }) == "手牌和路线对不上", "deviation_hand");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.ChoiceMismatch,
        }) == "游戏当前的选牌请求和路线里的选择对不上", "choice_mismatch");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.DeathTurn,
        }).Contains("死亡", StringComparison.Ordinal), "death_turn");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.LiveEndTurnRisk,
        }).Contains("实机复核", StringComparison.Ordinal), "live_end_turn_risk");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.NoSurvival,
        }).Contains("生还", StringComparison.Ordinal), "no_survival");
        Require(SolverPanelText.ErrorReason(new CombatFailureReason
        {
            Kind = CombatReasonKind.Unknown,
        }) == "详细原因已写入日志", "unknown_reason");
    }

    private static void PanelTextCatalog()
    {
        string[] missing = SolverPanelText.MappedSourceStrings
            .Where(static source => !SolverText.EnglishCatalogForTesting.ContainsKey(source))
            .ToArray();
        Require(missing.Length == 0, "missing:" + string.Join("|", missing));
        string[] oldCalculation = SolverText.EnglishCatalogForTesting.Keys
            .Concat(SolverPanelText.MappedSourceStrings)
            .Where(static source => source.Contains("计算", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Require(oldCalculation.Length == 0, "calculation:" + string.Join("|", oldCalculation));
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
            throw new InvalidDataException(code);
    }

    internal sealed record UiLogicCheckResult(int SchemaVersion, string Status, int Passed, int Failed,
        UiLogicCheck[] Checks);

    internal sealed record UiLogicCheck(string Name, string Status, string? Error);
}
