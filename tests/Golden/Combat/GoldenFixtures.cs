using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Quality;
namespace Sts2Solver.Golden;

/// <summary>Named native roots keep contracts independent of serialized request hashes.</summary>
internal static class GoldenFixtures
{
    internal static string DataDirectory { get; set; } = "tests/Golden/data";
    internal static string PathOf(string relative) => Path.Combine(DataDirectory, relative);
    internal static SearchCase Search(string root)
        => SearchConfig(PathOf($"d1/{root}/config.br"));

    internal static SearchCase SearchConfig(string path)
    {
        SearchCase source = SearchCodec.DecodeRequest(GoldenFiles.ReadCompressed(path));
        return source with
        {
            Options = source.Options with
            {
                BeamWidth = 160, MaxExpandedNodes = 120_000, MaxTurns = 64,
                SoftTimeBudget = TimeSpan.MaxValue,
                Ordering = new RouteOrdering(source.Battles[0].Player.CurrentHp,
                    source.Options.Scorer!.RulesForCodec.BossHpRelief,
                    source.Options.Scorer.RulesForCodec.PostCombatRelicHeal),
                InitialPlayerTurn = source.Battles[0].PlayerTurn,
            },
        };
    }
}
