using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Solver;

/// <summary>Checks against the game's real localization resources, in the native test host.</summary>
internal static class LocalizationChecks
{
    internal static void Run()
    {
        string original = LocManager.Instance.Language;
        try
        {
            LocManager.Instance.SetLanguage("zhs");
            Require(SolverText.Get("choice.exhaust")
                == new LocString("card_keywords", "EXHAUST.title").GetFormattedText(), "exhaust");
            Require(SolverText.Get("choice.transform")
                == new LocString("static_hover_tips", "TRANSFORM.title").GetFormattedText(), "transform");
            Require(SolverPanelText.ErrorReason(new CombatFailureReason
                { Kind = CombatReasonKind.Deviation, Code = "stars_drift" }).Contains(
                    new LocString("static_hover_tips", "STAR_COUNT.title").GetFormattedText(),
                    StringComparison.Ordinal), "stars");
            Require(CombatDisplayNames.CaptureEmpty().OstyName
                == ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.Osty>().Title.GetFormattedText(), "osty_zhs");

            CheckNames("unsupported-orb", ModelDb.All.OfType<OrbModel>()
                .Select(m => (m.Id.Entry, m.Title.GetFormattedText())));
            CheckNames("unsupported-enchantment", ModelDb.All.OfType<EnchantmentModel>()
                .Select(m => (m.Id.Entry, m.Title.GetFormattedText())));
            CheckNames("unsupported-affliction", ModelDb.All.OfType<AfflictionModel>()
                .Select(m => (m.Id.Entry, m.Title.GetFormattedText())));

            LocManager.Instance.SetLanguage("eng");
            Require(CombatDisplayNames.CaptureEmpty().PlayerName == "Player", "player_eng");
            Require(SolverText.Get("choice.exhaust") == "Exhaust", "exhaust_eng");
            Require(SolverText.Get("text.list_separator") == ", ", "separator_eng");
            Require(SolverText.Format("text.choice_suffix", "Strike") == " (Strike)", "choice_suffix_eng");

            LocManager.Instance.SetLanguage("fra");
            Require(SolverText.ResolveLanguage("xxx") == "eng", "unsupported_locale_english");
            Require(SolverText.Format("ui.elapsed_s", 1.5).Contains("1,5", StringComparison.Ordinal), "french_decimal_output");
            Require(SolverSettingsPage.TryParseMemoryGigabytes("2,6", 0, 20_000_000_000, out long? bytes)
                && bytes == 2_600_000_000, "french_decimal_input");
            Require(SolverSettingsPage.FormatGigabytes(2_600_000_000) == "2,6", "french_input_display");

            LocManager.Instance.SetLanguage("zht");
            Require(SolverText.Language == (SolverText.CatalogsForTesting.ContainsKey("zht") ? "zht" : "zhs"),
                "traditional_catalog_selection");
            Require(CombatDisplayNames.CaptureEmpty().OstyName
                == ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.Osty>().Title.GetFormattedText(), "osty_zht");
            LocManager.Instance.SetLanguage("zhs");
            Require(SolverText.Get("text.list_separator") == "、", "separator_restored");
            Require(CombatDisplayNames.CaptureEmpty().OstyName == "奥斯提", "name_restored");
        }
        finally
        {
            LocManager.Instance.SetLanguage(original);
        }
    }

    private static void CheckNames(string reason, IEnumerable<(string Id, string Title)> names)
    {
        foreach ((string id, string title) in names)
            Require(CombatDisplayNames.TryContentTitle(reason, id, out string actual) && actual == title,
                reason + ":" + id);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidDataException("localization:" + name);
    }
}
