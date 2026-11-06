// Structured failure details for the panel's reason line. The controller keeps stable codes and
// machine-readable detail envelopes (reason=, region=, capacity=, actual= and Battle diff keys);
// the panel maps those to localized sentences. Raw exception messages are never parsed for display.
namespace Sts2Solver;

internal static class CombatReasonKind
{
    internal const string Import = "import";
    internal const string ImportCapacity = "import_capacity";
    internal const string ImportUnsupported = "import_unsupported";
    internal const string InvalidData = "invalid_data";
    internal const string InvalidOperation = "invalid_operation";
    internal const string RequestInvalid = "request_invalid";
    internal const string RouteInvalid = "route_invalid";
    internal const string RouteUnsupported = "route_unsupported";
    internal const string RouteCapacity = "route_capacity";
    internal const string SearchNoRoute = "search_no_route";
    internal const string SearchMemoryLimit = "search_memory_limit";
    internal const string SearchCapacityBounded = "search_capacity_bounded";
    internal const string SearchFailed = "search_failed";
    internal const string ForcedPotion = "forced_potion";
    internal const string TurnSetupFailed = "turn_setup_failed";
    internal const string UnsupportedRuntime = "unsupported_runtime";
    internal const string CapacityRuntime = "capacity_runtime";
    internal const string Timeout = "timeout";
    internal const string Deployment = "deployment";
    internal const string Deviation = "deviation";
    internal const string ChoiceMismatch = "choice_mismatch";
    internal const string RepeatedFailure = "repeated_failure";
    internal const string DeathTurn = "death_turn";
    internal const string LiveEndTurnRisk = "live_end_turn_risk";
    internal const string NoSurvival = "no_survival";
    internal const string Unknown = "unknown";
}

/// <summary>One stable explanation behind a failed search or a stopped turn.</summary>
internal sealed record CombatFailureReason
{
    internal string Kind { get; init; } = CombatReasonKind.Unknown;

    /// <summary>Stable sub-code: <c>ImportReason</c>, deployment reason or coordinator status.</summary>
    internal string? Code { get; init; }

    /// <summary>Opaque machine detail used only for exact native-id lookup; never shown verbatim.</summary>
    internal string? Detail { get; init; }

    internal string? Region { get; init; }

    internal int? Capacity { get; init; }

    /// <summary>Actual item count when the failing capacity concerned a countable collection.</summary>
    internal int? Actual { get; init; }

    /// <summary>Battle field keys from a reconciliation diff.</summary>
    internal string[] Diff { get; init; } = [];

    internal CombatFailureReason? Inner { get; init; }
}

internal static class CombatFailureReasonClassifier
{
    internal static CombatFailureReason? Error(string? code, string? detail,
        SearchUiSnapshot? finishedSearch)
    {
        if (string.IsNullOrEmpty(code))
            return null;
        return code switch
        {
            "import_failed" => Import(detail),
            "import_capacity" => Capacity(CombatReasonKind.ImportCapacity, detail),
            "import_unsupported" => new() { Kind = CombatReasonKind.ImportUnsupported },
            "capacity" => Capacity(CombatReasonKind.CapacityRuntime, detail),
            "route_capacity" => Capacity(CombatReasonKind.RouteCapacity, detail),
            "route_invalid" => new() { Kind = CombatReasonKind.RouteInvalid },
            "route_unsupported" => new() { Kind = CombatReasonKind.RouteUnsupported },
            "request_invalid" => new() { Kind = CombatReasonKind.RequestInvalid },
            "invalid_data" => new() { Kind = CombatReasonKind.InvalidData },
            "invalid_operation" => new() { Kind = CombatReasonKind.InvalidOperation },
            "unsupported" or "not_supported" => new() { Kind = CombatReasonKind.UnsupportedRuntime },
            "timeout" => new() { Kind = CombatReasonKind.Timeout },
            "turn_setup_failed" => new() { Kind = CombatReasonKind.TurnSetupFailed },
            "search_failed" => Search(finishedSearch),
            "forced_potion_unusable" => new() { Kind = CombatReasonKind.ForcedPotion },
            _ => new() { Kind = CombatReasonKind.Unknown, Code = code, Detail = detail },
        };
    }

    internal static CombatFailureReason? Pause(string? code, string? detail,
        CombatFailureReason? errorReason)
    {
        if (code is null)
            return null;
        return code switch
        {
            "action_deviation" => Deviation(null, detail),
            "deployment_deviation" => Deviation(BeforeColon(detail), detail),
            "boundary_deviation" => Deviation(null, detail),
            "deploy_failed" => Deployment(BeforeColon(detail)),
            "choice_mismatch" => new() { Kind = CombatReasonKind.ChoiceMismatch },
            "repeated_failure" => new()
            {
                Kind = CombatReasonKind.RepeatedFailure,
                Inner = errorReason,
            },
            "death_turn" => new() { Kind = CombatReasonKind.DeathTurn },
            "live_end_turn_risk" => new() { Kind = CombatReasonKind.LiveEndTurnRisk },
            "no_survival" => new() { Kind = CombatReasonKind.NoSurvival },
            _ => new() { Kind = CombatReasonKind.Unknown, Code = code, Detail = detail },
        };
    }

    private static CombatFailureReason Import(string? detail)
    {
        (string? reason, string? remainder) = PrefixToken(detail, "reason=");
        return new()
        {
            Kind = CombatReasonKind.Import,
            Code = reason,
            Detail = remainder,
        };
    }

    private static CombatFailureReason Capacity(string kind, string? detail)
    {
        string? region = null;
        int? capacity = null;
        int? actual = null;
        foreach (string token in (detail ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("region=", StringComparison.Ordinal))
                region = token["region=".Length..];
            else if (token.StartsWith("capacity=", StringComparison.Ordinal))
                capacity = ParseInt(token["capacity=".Length..]);
            else if (token.StartsWith("actual=", StringComparison.Ordinal))
                actual = ParseInt(token["actual=".Length..]);
        }
        return new()
        {
            Kind = kind,
            Detail = detail,
            Region = region,
            Capacity = capacity,
            Actual = actual,
        };
    }

    private static CombatFailureReason Search(SearchUiSnapshot? search)
    {
        if (search?.SearchStatus == "capacitybounded")
            return new() { Kind = CombatReasonKind.SearchCapacityBounded };
        if (search?.MemoryBudgetExceeded == true)
            return new() { Kind = CombatReasonKind.SearchMemoryLimit };
        if (search?.SearchStatus == "incomplete")
            return new() { Kind = CombatReasonKind.SearchNoRoute };
        return new() { Kind = CombatReasonKind.SearchFailed };
    }

    private static CombatFailureReason Deployment(string? reason)
        => new() { Kind = CombatReasonKind.Deployment, Code = reason };

    private static CombatFailureReason Deviation(string? code, string? detail)
    {
        string[] diff = SplitDiff(detail);
        // A forced deployment failure has no Battle diff; its detail is a bare reason token.
        if (code is null && diff.Length == 1
            && !diff[0].Contains('=') && !diff[0].Contains(':'))
        {
            code = diff[0];
            diff = [];
        }
        return new()
        {
            Kind = CombatReasonKind.Deviation,
            Code = code,
            Detail = detail,
            Diff = diff,
        };
    }

    private static string[] SplitDiff(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
            return [];
        string[] lines = detail.Split(" | ", StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < lines.Length; index++)
            lines[index] = DiffKey(lines[index]);
        return lines;
    }

    /// <summary>Extracts the Battle field key from a <c>+00012 Key: predicted=... live=...</c> line.</summary>
    private static string DiffKey(string line)
    {
        int space = line.IndexOf(' ');
        if (space < 0)
            return line;
        int start = space + 1;
        int colon = line.IndexOf(':', start);
        return colon < 0 ? line[start..] : line[start..colon];
    }

    private static string? BeforeColon(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
            return null;
        int colon = detail.IndexOf(':');
        return colon <= 0 ? null : detail[..colon].Trim();
    }

    private static (string? Token, string? Remainder) PrefixToken(string? detail, string prefix)
    {
        if (string.IsNullOrEmpty(detail) || !detail.StartsWith(prefix, StringComparison.Ordinal))
            return (null, detail);
        string body = detail[prefix.Length..].Trim();
        int space = body.IndexOf(' ');
        return space < 0
            ? (body, null)
            : (body[..space], body[(space + 1)..].Trim());
    }

    private static int? ParseInt(string text)
        => int.TryParse(text, out int value) ? value : null;
}
