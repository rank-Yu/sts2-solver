// Adapted from CombatSolver src/UI/SolverText.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Catalogs use stable keys and are embedded separately for each game language.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2Solver;

/// <summary>UI text only. Game content names come from the native localization models.</summary>
internal static partial class SolverText
{
    private const string ResourcePrefix = "Sts2Solver.UI.Locales.";
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalogs = LoadCatalogs();

    internal static string Language => ResolveLanguage(LocManager.Instance.Language);

    internal static CultureInfo Culture => LocManager.Instance.CultureInfo;

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> CatalogsForTesting => Catalogs;

    // Until their own catalogs are supplied, traditional Chinese uses simplified Chinese,
    // and other game languages use English. Missing keys in an available catalog are errors.
    internal static string ResolveLanguage(string language)
        => Catalogs.ContainsKey(language) ? language : language == "zht" ? "zhs" : "eng";

    internal static void Initialize()
        => ModLog.Info($"[Sts2Solver] LOCALE_CATALOG languages={Catalogs.Count} entries={Catalogs["eng"].Count}");

    public static string Get(string key) => Catalogs[Language][key];

    public static string Format(string key, params object?[] arguments)
        => string.Format(Culture, Get(key), arguments);

    internal static string[] ValidateCatalogs()
    {
        List<string> problems = [];
        IReadOnlyDictionary<string, string> english = Catalogs["eng"];
        foreach ((string language, IReadOnlyDictionary<string, string> catalog) in Catalogs)
        {
            foreach (string key in english.Keys.Except(catalog.Keys))
                problems.Add($"{language}:missing:{key}");
            foreach (string key in catalog.Keys.Except(english.Keys))
                problems.Add($"{language}:extra:{key}");
            foreach ((string key, string value) in catalog)
            {
                if (string.IsNullOrWhiteSpace(value))
                    problems.Add($"{language}:empty:{key}");
                _ = CompositeFormat.Parse(value);
                if (english.TryGetValue(key, out string? reference)
                    && !FormatItems(value).SequenceEqual(FormatItems(reference)))
                    problems.Add($"{language}:placeholders:{key}");
            }
        }
        return [.. problems];
    }

    private static int[] FormatItems(string template)
        => FormatItemPattern().Matches(template).Select(match => int.Parse(match.Groups[1].Value,
            CultureInfo.InvariantCulture)).Order().ToArray();

    [GeneratedRegex(@"\{([0-9]+)(?:,[^{}]*)?(?::[^{}]*)?\}")]
    private static partial Regex FormatItemPattern();

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadCatalogs()
    {
        var assembly = typeof(SolverText).Assembly;
        Dictionary<string, IReadOnlyDictionary<string, string>> catalogs = new(StringComparer.Ordinal);
        foreach (string resource in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                         && name.EndsWith(".json", StringComparison.Ordinal)))
        {
            string language = resource[ResourcePrefix.Length..^5];
            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException($"缺少文案资源：{resource}");
            catalogs.Add(language, JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidDataException($"文案目录为空：{resource}"));
        }
        return catalogs;
    }
}
