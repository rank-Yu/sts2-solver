// Adapted from CombatSolver src/UI/SolverText.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The catalog is rebuilt for Sts2Solver and missing keys fall back to the Chinese source string.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2Solver;

/// <summary>
/// UI-only source-string catalog. Format templates before inserting game names or player input.
/// Chinese source strings are the keys; <c>English.json</c> is an embedded resource.
/// </summary>
internal static partial class SolverText
{
    private static readonly IReadOnlyDictionary<string, string> English = LoadEnglish();
    private static readonly HashSet<string> MissingKeys = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    internal static bool IsEnglish
        => LocManager.Instance is { } manager && manager.Language is not ("zhs" or "zht");

    internal static IReadOnlyDictionary<string, string> EnglishCatalogForTesting => English;

    /// <summary>Source strings that were requested in English but have no catalog entry.</summary>
    internal static string[] MissingKeysForTesting
    {
        get
        {
            lock (Gate)
                return [.. MissingKeys];
        }
    }

    internal static void Initialize()
        => ModLog.Info($"[Sts2Solver] LOCALE_CATALOG entries={English.Count}");

    public static string Get(string source)
    {
        if (!IsEnglish)
            return source;
        if (English.TryGetValue(source, out string? translated))
            return translated;
        lock (Gate)
        {
            if (MissingKeys.Add(source))
                ModLog.Warn($"[Sts2Solver] LOCALE_KEY_MISSING key={source}");
        }
        return source;
    }

    public static string Format(FormattableString source)
        => string.Format(CultureInfo.CurrentCulture, Get(source.Format), source.GetArguments());

    /// <summary>Returns catalog problems; used by the headless localization check.</summary>
    internal static string[] ValidateEnglishCatalog()
    {
        if (English.Count == 0)
            return ["catalog_empty"];
        List<string> problems = [];
        foreach ((string source, string translated) in English)
        {
            if (string.IsNullOrEmpty(translated))
            {
                problems.Add($"empty_translation:{source}");
                continue;
            }
            int[] sourceItems = FormatItems(source);
            int[] translatedItems = FormatItems(translated);
            if (!sourceItems.SequenceEqual(translatedItems))
                problems.Add($"placeholder_mismatch:{source}");
        }
        return [.. problems];
    }

    private static int[] FormatItems(string template)
    {
        List<int> items = [];
        foreach (Match match in FormatItemPattern().Matches(template))
            items.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        items.Sort();
        return [.. items];
    }

    [GeneratedRegex(@"\{([0-9]+)(?:,[^{}]*)?(?::[^{}]*)?\}")]
    private static partial Regex FormatItemPattern();

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        try
        {
            using Stream? stream = typeof(SolverText).Assembly
                .GetManifestResourceStream("Sts2Solver.UI.English.json");
            if (stream == null)
            {
                ModLog.Error("[Sts2Solver] LOCALE_CATALOG_MISSING resource=Sts2Solver.UI.English.json");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidDataException("英文目录为空。");
        }
        catch (IOException exception)
        {
            ModLog.Error($"[Sts2Solver] LOCALE_CATALOG_MISSING reason=io detail={exception.Message}");
        }
        catch (JsonException exception)
        {
            ModLog.Error($"[Sts2Solver] LOCALE_CATALOG_MISSING reason=json detail={exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            ModLog.Error($"[Sts2Solver] LOCALE_CATALOG_MISSING reason=unsupported detail={exception.Message}");
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
