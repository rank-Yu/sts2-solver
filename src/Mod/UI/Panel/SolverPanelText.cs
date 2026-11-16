using Sts2Solver.Import;

namespace Sts2Solver;

/// <summary>
/// Maps the snapshot's stable codes (phase, end reason, error, pause and choice labels) to
/// localized player text. Unknown codes are shown verbatim inside a localized template, so a new
/// code is visible instead of being mistaken for a known one.
/// </summary>
internal static class SolverPanelText
{
    private static readonly IReadOnlyDictionary<string, string> ErrorNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["import_failed"] = "ui.search_setup_failed",
        ["import_unsupported"] = "ui.search_setup_failed",
        ["import_capacity"] = "ui.search_setup_failed",
        ["request_invalid"] = "ui.the_search_request_is_invalid",
        ["route_invalid"] = "ui.the_search_result_could_not_be_replayed_and_was_discarded",
        ["route_unsupported"] = "ui.the_route_contains_an_effect_that_is_not_supported_yet",
        ["route_capacity"] = "ui.replaying_the_route_exceeded_the_state_capacity",
        ["search_failed"] = "ui.search_failed",
        ["turn_setup_failed"] = "ui.turn_start_choice_failed",
        ["boundary_deviation"] = "ui.the_turn_started_differently_than_predicted_stopped",
        ["action_deviation"] = "ui.an_action_resolved_differently_than_predicted_stopped",
        ["deploy_failed"] = "ui.automatic_execution_stopped",
        ["cancelled"] = "ui.search_cancelled",
        ["pilot_failed"] = "ui.playing_the_combat_ended_abnormally",
        ["unsupported_import"] = "ui.this_combat_contains_content_that_is_not_supported_yet",
        ["unsupported"] = "ui.hit_game_behavior_that_is_not_supported_yet",
        ["capacity"] = "ui.exceeded_the_solver_s_state_capacity",
        ["not_supported"] = "ui.hit_game_behavior_that_is_not_supported_yet",
        ["invalid_data"] = "ui.combat_data_failed_validation",
        ["invalid_operation"] = "ui.the_current_state_does_not_allow_playing_on",
        ["timeout"] = "ui.timed_out_waiting_for_the_game",
    };

    private static readonly IReadOnlyDictionary<string, string> PauseNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["action_deviation"] = "ui.an_action_resolved_differently_than_predicted_stopped",
        ["boundary_deviation"] = "ui.the_turn_started_differently_than_predicted_stopped",
        ["deployment_deviation"] = "ui.the_live_state_no_longer_matches_the_route_stopped",
        ["deploy_failed"] = "ui.automatic_execution_stopped",
    };

    /// <summary>Stable import rejection reasons (see ImportReason).</summary>
    private static readonly IReadOnlyDictionary<string, string> ImportReasonNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["multi-player"] = "ui.this_is_a_multiplayer_game_the_solver_supports_single_player",
        ["root-phase"] = "ui.it_is_not_currently_the_player_s_play_phase",
        ["pending-choice"] = "ui.a_card_choice_is_still_pending",
        ["unsupported-closure"] = "ui.this_combat_contains_an_unsupported_card_effect_or_power_combination",
        ["missing-mirror-risk"] = "ui.unsupported_prediction_effect",
        ["unrepresentable-state"] = "ui.the_current_combat_state_cannot_be_represented",
        ["capacity"] = "ui.the_combat_content_exceeds_the_solver_s_capacity",
        ["third-party-content"] = "ui.this_combat_contains_third_party_mod_content",
        ["missing-private-field"] = "ui.could_not_read_the_game_s_internal_combat_state",
        ["unclaimed-private-state"] = "ui.the_combat_state_contains_private_data_the_solver_does_not",
        ["rules-mismatch"] = "ui.the_game_rules_do_not_match_the_solver_s_expectations",
        ["known-enemy-history"] = "ui.the_monster_s_action_history_cannot_be_represented",
        ["prefix-plan"] = "ui.the_opening_prefix_route_cannot_be_represented",
        ["turn-setup-root"] = "ui.the_turn_start_choices_cannot_be_assembled_into_a_search",
        ["run-deck-hook"] = "ui.an_out_of_combat_deck_effect_cannot_be_represented",
        ["test-content"] = "ui.this_combat_contains_test_content",
        ["deprecated-content"] = "ui.this_combat_contains_deprecated_content",
        ["unsupported-policy"] = "ui.the_search_policy_cannot_be_represented",
        ["unsupported-healing-certificate"] = "ui.the_healing_cap_cannot_be_represented",
    };

    /// <summary>Fallback category sentences for unsupported content without a resolvable native id.</summary>
    private static readonly IReadOnlyDictionary<string, string> ContentFallbackNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["unsupported-card"] = "ui.this_combat_contains_an_unsupported_card",
            ["unsupported-power"] = "ui.this_combat_contains_an_unsupported_power",
            ["unsupported-relic"] = "ui.this_combat_contains_an_unsupported_relic",
            ["unsupported-potion"] = "ui.this_combat_contains_an_unsupported_potion",
            ["unsupported-orb"] = "ui.this_combat_contains_an_unsupported_orb",
            ["unsupported-monster"] = "ui.this_combat_contains_an_unsupported_monster",
            ["unsupported-enchantment"] = "ui.this_combat_contains_an_unsupported_enchantment",
            ["unsupported-affliction"] = "ui.this_combat_contains_an_unsupported_affliction",
        };

    private static readonly IReadOnlyDictionary<string, string> ContentNameFormats =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["unsupported-card"] = "ui.this_combat_contains_an_unsupported_card_named",
            ["unsupported-power"] = "ui.this_combat_contains_an_unsupported_power_named",
            ["unsupported-relic"] = "ui.this_combat_contains_an_unsupported_relic_named",
            ["unsupported-potion"] = "ui.this_combat_contains_an_unsupported_potion_named",
            ["unsupported-orb"] = "ui.this_combat_contains_an_unsupported_orb_named",
            ["unsupported-monster"] = "ui.this_combat_contains_an_unsupported_monster_named",
            ["unsupported-enchantment"] = "ui.this_combat_contains_an_unsupported_enchantment_named",
            ["unsupported-affliction"] = "ui.this_combat_contains_an_unsupported_affliction_named",
        };

    /// <summary>Deployment failure reason codes from ActionDeployer / CombatTurnDeployer.</summary>
    private static readonly IReadOnlyDictionary<string, string> DeploymentReasonNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action_failed"] = "ui.a_native_action_failed",
            ["action_canceled"] = "ui.the_game_cancelled_a_native_action",
            ["action_not_enqueued"] = "ui.the_action_did_not_enter_the_game_s_action_queue",
            ["boundary_timeout"] = "ui.timed_out_waiting_for_the_game_to_resolve",
            ["live_missing"] = "ui.the_combat_state_could_not_be_found",
            ["live_player_missing"] = "ui.the_combat_state_could_not_be_found",
            ["combat_changed"] = "ui.the_combat_state_changed_or_ended",
            ["combat_over"] = "ui.the_combat_state_changed_or_ended",
            ["combat_ended"] = "ui.the_combat_state_changed_or_ended",
            ["import_failed"] = "ui.the_combat_state_could_not_be_read_after_the_action",
            ["import_unsupported"] = "ui.the_combat_state_could_not_be_read_after_the_action",
            ["import_capacity"] = "ui.the_combat_state_could_not_be_read_after_the_action",
            ["import_invalid_operation"] = "ui.the_combat_state_could_not_be_read_after_the_action",
            ["not_main_thread"] = "ui.deployment_did_not_run_on_the_game_s_main_thread",
            ["action_kind"] = "ui.the_route_contains_an_unknown_action_type",
        };

    /// <summary>Deployment drift reason codes; the same names appear in the stopped summary.</summary>
    private static readonly IReadOnlyDictionary<string, string> DeviationReasonNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["turn_drift"] = "ui.the_turn_does_not_match_the_route",
            ["energy_drift"] = "ui.energy_does_not_match_the_route",
            ["stars_drift"] = "ui.stars_do_not_match_the_route",
            ["hand_drift"] = "ui.the_hand_does_not_match_the_route",
            ["potion_slot_drift"] = "ui.the_potion_slots_do_not_match_the_route",
            ["card_missing"] = "ui.a_card_in_the_route_is_not_in_hand",
            ["card_mismatch"] = "ui.the_hand_does_not_match_the_route",
            ["card_unplayable"] = "ui.a_card_in_the_route_cannot_be_played_now",
            ["potion_missing"] = "ui.a_potion_in_the_route_is_not_in_the_potion",
            ["potion_mismatch"] = "ui.the_potions_do_not_match_the_route",
            ["potion_target"] = "ui.the_potion_target_does_not_match_the_route",
            ["target_missing"] = "ui.a_target_in_the_route_does_not_exist",
        };

    /// <summary>Battle capacity regions translated for the reason line.</summary>
    private static readonly IReadOnlyDictionary<string, string> CapacityRegionNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cards"] = "ui.deck",
            ["Enemies"] = "ui.enemies",
            ["Powers"] = "ui.powers",
            ["Relics"] = "ui.relic_count",
            ["Orbs"] = "ui.orbs",
            ["PotionSlots"] = "ui.potion_slots",
            ["MonsterRules"] = "ui.monster_rules",
            ["Choice.Options"] = "ui.choices",
            ["Choice.GeneratedOptions"] = "ui.generated_choices",
            ["GeneratedCards"] = "ui.generated_cards",
            ["DrawnCards"] = "ui.card_draws",
            ["AutoPlayCards"] = "ui.auto_played_cards",
            ["AttackWaves"] = "ui.attack_waves",
            ["Card.EnergyModifiers"] = "ui.card_energy_modifiers",
            ["Card.ReplayCount"] = "ui.card_replay_counts",
            ["Card.DecimalVars"] = "ui.card_decimal_variables",
            ["Card.TemporaryStarCosts"] = "ui.temporary_card_star_costs",
            ["Beam.Pool"] = "ui.search_candidate_pool",
            ["TransientMarks"] = "ui.transient_marks",
            ["DetachedCards"] = "ui.detached_cards",
            ["EncounterSpawns"] = "ui.encounter_spawns",
            ["PowerCommitment.Cards"] = "ui.power_commitment_cards",
        };

    private static readonly IReadOnlyDictionary<string, string> ChoiceEffectNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Discard"] = "choice.discard",
        ["DiscardAndDraw"] = "choice.discard",
        ["Exhaust"] = "choice.exhaust",
        ["Transform"] = "choice.transform",
        ["MoveToHand"] = "choice.choose",
        ["MoveToHandFreeThisTurn"] = "choice.choose",
        ["GenerateToHand"] = "choice.choose",
        ["Upgrade"] = "choice.upgrade",
        ["Duplicate"] = "choice.duplicate",
        ["SetFreeThisCombat"] = "choice.choose",
        ["AutoPlayRepeated"] = "choice.choose",
        ["ApplySly"] = "choice.choose",
        ["ApplyRetain"] = "choice.choose",
        ["ApplyEthereal"] = "choice.choose",
        ["Nightmare"] = "choice.choose",
        ["Modify"] = "choice.choose",
        ["ApplyKnowledgeCurse"] = "choice.choose",
    };

    /// <summary>Every localization key used by the maps above.</summary>
    internal static IEnumerable<string> MappedKeys
        => ErrorNames.Values.Concat(PauseNames.Values)
            .Concat(ChoiceEffectNames.Values)
            .Concat(ImportReasonNames.Values).Concat(ContentFallbackNames.Values)
            .Concat(ContentNameFormats.Values).Concat(DeploymentReasonNames.Values)
            .Concat(DeviationReasonNames.Values).Concat(CapacityRegionNames.Values)
            .Distinct(StringComparer.Ordinal);

    internal static string Error(string code)
        => ErrorNames.TryGetValue(code, out string? source)
            ? SolverText.Get(source)
            : SolverText.Format("ui.error", code);

    /// <summary>
    /// Warning under a route summary when at least one member dropped children at a Battle
    /// capacity. The route remains valid; only the explored set was incomplete.
    /// </summary>
    internal static string CapacityDropHint(IReadOnlyDictionary<string, int>? regions)
    {
        if (regions is not { Count: > 0 })
            return string.Empty;
        List<string> names = [];
        foreach (string region in regions.Keys)
        {
            string name = region == "Cards"
                ? SolverText.Get("ui.card_count")
                : CapacityRegionNames.TryGetValue(region, out string? translated)
                    ? SolverText.Get(translated)
                    : SolverText.Get("ui.combat_state");
            if (!names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }
        string joined = string.Join(SolverText.Get("text.list_separator"), names);
        return SolverText.Format("ui.some_routes_were_skipped_because_the_exceeded_the_solver_capacity", joined);
    }

    /// <summary>
    /// One concrete line below a failed search or stopped turn. Structured details are mapped to
    /// player language; anything without a mapping points at the full log entry instead.
    /// </summary>
    internal static string ErrorReason(CombatFailureReason? reason)
    {
        if (reason is null)
            return SolverText.Get("ui.full_details_are_in_the_log");
        return reason.Kind switch
        {
            CombatReasonKind.Import => ImportReasonText(reason),
            CombatReasonKind.ImportCapacity => CapacityText(reason),
            CombatReasonKind.CapacityRuntime => CapacityText(reason),
            CombatReasonKind.RouteCapacity => CapacityText(reason),
            CombatReasonKind.ImportUnsupported => SolverText.Get("ui.this_combat_contains_content_the_solver_does_not_support"),
            CombatReasonKind.InvalidData => SolverText.Get("ui.the_combat_data_returned_by_the_game_failed_validation"),
            CombatReasonKind.InvalidOperation => SolverText.Get("ui.the_current_game_state_does_not_allow_continuing"),
            CombatReasonKind.RequestInvalid => SolverText.Get("ui.the_search_request_is_invalid"),
            CombatReasonKind.RouteInvalid => SolverText.Get("ui.the_route_returned_by_the_search_cannot_be_replayed"),
            CombatReasonKind.RouteUnsupported => SolverText.Get("ui.the_search_route_contains_an_effect_the_solver_does_not"),
            CombatReasonKind.SearchNoRoute => SolverText.Get("ui.no_executable_route_was_found"),
            CombatReasonKind.SearchMemoryLimit => SolverText.Get("ui.the_search_reached_the_memory_limit_without_finding_an_executable"),
            CombatReasonKind.SearchCapacityBounded => SolverText.Get("ui.the_solver_generated_too_many_cards_and_exceeded_its_capacity"),
            CombatReasonKind.SearchFailed => SolverText.Get("ui.the_search_failed_before_returning_an_executable_route"),
            CombatReasonKind.TurnSetupFailed => SolverText.Get("ui.no_executable_turn_start_choice_plan_was_found"),
            CombatReasonKind.UnsupportedRuntime => SolverText.Get("ui.the_search_encountered_game_behavior_the_solver_does_not_support"),
            CombatReasonKind.Timeout => SolverText.Get("ui.timed_out_waiting_for_the_game"),
            CombatReasonKind.Deployment => DeploymentReasonText(reason),
            CombatReasonKind.Deviation => DeviationReasonText(reason),
            CombatReasonKind.ChoiceMismatch => SolverText.Get("ui.the_game_s_current_choice_request_does_not_match_the"),
            CombatReasonKind.RepeatedFailure => reason.Inner is { } inner
                ? ErrorReason(inner)
                : SolverText.Get("ui.the_search_failed_repeatedly"),
            CombatReasonKind.DeathTurn => SolverText.Get("ui.the_route_predicts_death_this_turn"),
            CombatReasonKind.LiveEndTurnRisk => SolverText.Get("ui.the_live_end_turn_check_found_death_or_more_hp"),
            CombatReasonKind.NoSurvival => SolverText.Get("ui.no_surviving_route_the_route_is_predicted_to_die"),
            _ => SolverText.Get("ui.full_details_are_in_the_log"),
        };
    }

    private static string ImportReasonText(CombatFailureReason reason)
    {
        if (reason.Code is { } code
            && ContentFallbackNames.TryGetValue(code, out string? fallback)
            && ContentNameFormats.TryGetValue(code, out string? named))
        {
            return CombatDisplayNames.TryContentTitle(code, reason.Detail, out string title)
                ? string.Format(SolverText.Culture, SolverText.Get(named), title)
                : SolverText.Get(fallback);
        }
        return reason.Code is { } known && ImportReasonNames.TryGetValue(known, out string? source)
            ? SolverText.Get(source)
            : SolverText.Get("ui.full_details_are_in_the_log");
    }

    private static string CapacityText(CombatFailureReason reason)
    {
        if (reason.Region == "Cards")
        {
            if (reason.Actual is { } actual && reason.Capacity is { } capacity)
                return SolverText.Format("ui.the_deck_has_cards_the_solver_limit_is", actual, capacity);
            if (reason.Capacity is { } onlyCapacity)
                return SolverText.Format("ui.the_deck_exceeds_the_solver_limit_of_cards", onlyCapacity);
            if (reason.Actual is { } onlyActual)
                return SolverText.Format("ui.the_deck_has_cards_and_exceeds_the_solver_s_capacity", onlyActual);
            return SolverText.Get("ui.the_deck_exceeds_the_solver_s_capacity");
        }
        string region = reason.Region is { } name
            && CapacityRegionNames.TryGetValue(name, out string? translated)
            ? SolverText.Get(translated)
            : SolverText.Get("ui.combat_state");
        return reason.Capacity is { } limit
            ? SolverText.Format("ui.the_exceeded_the_solver_limit_of", region, limit)
            : SolverText.Get("ui.the_combat_state_exceeded_the_solver_s_capacity");
    }

    private static string DeploymentReasonText(CombatFailureReason reason)
        => reason.Code is { } code && DeploymentReasonNames.TryGetValue(code, out string? source)
            ? SolverText.Get(source)
            : SolverText.Get("ui.full_details_are_in_the_log");

    private static string DeviationReasonText(CombatFailureReason reason)
    {
        if (reason.Code is { } code && DeviationReasonNames.TryGetValue(code, out string? source))
            return SolverText.Get(source);
        bool hand = false, energy = false, stars = false, potions = false, turn = false;
        bool enemies = false, powers = false, relics = false, pile = false, life = false;
        foreach (string key in reason.Diff)
        {
            if (key is "HandCount" || key.StartsWith("Hand[", StringComparison.Ordinal))
                hand = true;
            else if (key == "Energy")
                energy = true;
            else if (key == "Stars")
                stars = true;
            else if (key is "PotionSlotCount" || key.StartsWith("Potion[", StringComparison.Ordinal))
                potions = true;
            else if (key is "PlayerTurn" or "Round" or "Side" or "Phase")
                turn = true;
            else if (key is "EnemyCount" or "RosterCount"
                     || key.StartsWith("Enemy[", StringComparison.Ordinal)
                     || key.StartsWith("Roster[", StringComparison.Ordinal))
                enemies = true;
            else if (key is "PowerCount" || key.StartsWith("Power[", StringComparison.Ordinal))
                powers = true;
            else if (key is "RelicStateCount"
                     || key.StartsWith("RelicStates[", StringComparison.Ordinal))
                relics = true;
            else if (key.StartsWith("Player.", StringComparison.Ordinal)
                     || key.StartsWith("Osty.", StringComparison.Ordinal))
                life = true;
            else if (key is "DrawCount" or "DiscardCount" or "ExhaustCount" or "PlayCount"
                     || key.StartsWith("Draw[", StringComparison.Ordinal)
                     || key.StartsWith("Discard[", StringComparison.Ordinal)
                     || key.StartsWith("Exhaust[", StringComparison.Ordinal)
                     || key.StartsWith("Play[", StringComparison.Ordinal))
                pile = true;
        }
        List<string> parts = [];
        if (hand) parts.Add(SolverText.Get("ui.the_hand_does_not_match_the_route"));
        if (energy) parts.Add(SolverText.Get("ui.energy_does_not_match_the_route"));
        if (stars) parts.Add(SolverText.Get("ui.stars_do_not_match_the_route"));
        if (potions) parts.Add(SolverText.Get("ui.the_potions_do_not_match_the_route"));
        if (turn) parts.Add(SolverText.Get("ui.the_turn_does_not_match_the_route"));
        if (enemies) parts.Add(SolverText.Get("ui.the_enemies_do_not_match_the_route"));
        if (powers) parts.Add(SolverText.Get("ui.the_powers_do_not_match_the_route"));
        if (relics) parts.Add(SolverText.Get("ui.the_relics_do_not_match_the_route"));
        if (life) parts.Add(SolverText.Get("ui.hp_does_not_match_the_route"));
        if (pile) parts.Add(SolverText.Get("ui.the_card_piles_do_not_match_the_route"));
        return parts.Count > 0
            ? string.Join(SolverText.Get("text.list_separator"), parts)
            : SolverText.Get("ui.the_live_state_does_not_match_the_route");
    }

    internal static string Pause(string? code, int turn)
        => code is not null && PauseNames.TryGetValue(code, out string? pauseSource)
            ? SolverText.Get(pauseSource)
            : code switch
            {
                "turn_limit" => SolverText.Get("ui.turn_limit_exceeded_playing_paused"),
                "capacity_bounded" => SolverText.Format("ui.turn_the_search_exceeded_the_combat_state_capacity_playing_paused", turn),
                "repeated_failure" => SolverText.Format("ui.turn_repeated_failures_playing_paused", turn),
                "choice_mismatch" => SolverText.Get("ui.the_card_choice_plan_did_not_match_the_live_request"),
                "death_turn" => SolverText.Format("ui.turn_is_projected_to_be_fatal_playing_paused", turn),
                "live_end_turn_risk" => SolverText.Get("ui.the_live_end_turn_check_found_a_risk_playing_paused"),
                "no_survival" => SolverText.Get("ui.no_surviving_route_playing_paused"),
                "stopped" => SolverText.Get("ui.search_stopped"),
                null => SolverText.Get("ui.playing_paused"),
                _ => SolverText.Format("ui.playing_paused_reason", code),
            };

    internal static string ChoiceEffect(string code)
        => ChoiceEffectNames.TryGetValue(code, out string? source) ? SolverText.Get(source) : code;

    /// <summary>Why the search slot is disabled; codes come from the controller's CanSolve.</summary>
    internal static string SearchBlock(string? code) => code switch
    {
        "enemy_turn" => SolverText.Get("ui.enemy_turn_search_when_your_turn_begins"),
        "actions_running" => SolverText.Get("ui.wait_for_the_current_action_to_finish"),
        "turn_start" => SolverText.Get("ui.turn_start_is_resolving_search_after_the_opening_choice"),
        "turn_setup" => SolverText.Get("ui.waiting_for_turn_start_choice"),
        "executing" => SolverText.Get("ui.stop_automatic_play_first"),
        "searching" => SolverText.Get("ui.the_search_is_still_winding_down_please_wait"),
        "multiplayer" => SolverText.Get("ui.search_is_unavailable_in_multiplayer"),
        "not_playable" => SolverText.Get("ui.search_is_unavailable_in_the_current_combat_state"),
        "not_in_combat" => SolverText.Get("ui.not_in_combat"),
        _ => SolverText.Get("ui.search_is_unavailable_in_the_current_state"),
    };

    internal static string Tier(SearchPerformanceTier tier) => tier switch
    {
        SearchPerformanceTier.Low => SolverText.Get("ui.low"),
        SearchPerformanceTier.Medium => SolverText.Get("ui.medium"),
        SearchPerformanceTier.High => SolverText.Get("ui.high"),
        SearchPerformanceTier.ExtraHigh => SolverText.Get("ui.extra_high"),
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };

    internal static string MemorySource(SearchMemoryBudgetSource source) => source switch
    {
        SearchMemoryBudgetSource.Settings => SolverText.Get("settings.title"),
        SearchMemoryBudgetSource.Default => SolverText.Get("ui.default"),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };
}
