using System.Globalization;

namespace Sts2SimExport;

internal sealed record ExportOptions
{
    internal const string DefaultCharacters = "IRONCLAD,SILENT,DEFECT,REGENT,NECROBINDER";

    internal string OutputDirectory { get; init; } = Path.Combine(".local", "sts2-sim-export");
    internal int Runs { get; init; } = 20;
    internal int TargetCombats { get; init; }
    internal int MaxFloors { get; init; } = 60;
    internal string SeedPrefix { get; init; } = "FS1";
    internal string[] Characters { get; init; } = DefaultCharacters.Split(',');
    internal int[] Ascensions { get; init; } = [0, 10];
    internal bool Resume { get; init; }
    internal bool ForceWin { get; init; }
    internal bool RandomizeForceWin { get; init; }

    internal static bool TryParse(string[] args, out ExportOptions options, out string error)
    {
        options = new ExportOptions();
        error = string.Empty;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--out" when index + 1 < args.Length:
                    options = options with { OutputDirectory = args[++index] };
                    break;
                case "--runs" when index + 1 < args.Length:
                    if (!TryInt(args[++index], out int runs) || runs < 1)
                    {
                        error = "--runs 必须是正整数。";
                        return false;
                    }
                    options = options with { Runs = runs };
                    break;
                case "--target-combats" when index + 1 < args.Length:
                    if (!TryInt(args[++index], out int target) || target < 0)
                    {
                        error = "--target-combats 必须是非负整数。";
                        return false;
                    }
                    options = options with { TargetCombats = target };
                    break;
                case "--max-floors" when index + 1 < args.Length:
                    if (!TryInt(args[++index], out int floors) || floors < 1)
                    {
                        error = "--max-floors 必须是正整数。";
                        return false;
                    }
                    options = options with { MaxFloors = floors };
                    break;
                case "--seed-prefix" when index + 1 < args.Length:
                    options = options with { SeedPrefix = args[++index] };
                    break;
                case "--characters" when index + 1 < args.Length:
                    string[] characters = args[++index]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(static value => value.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
                    if (characters.Length == 0)
                    {
                        error = "--characters 至少需要一个角色。";
                        return false;
                    }
                    options = options with { Characters = characters };
                    break;
                case "--ascensions" when index + 1 < args.Length:
                    int[] ascensions = args[++index]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(static value => int.Parse(value, CultureInfo.InvariantCulture)).Distinct().ToArray();
                    if (ascensions.Length == 0 || ascensions.Any(static value => value is < 0 or > 10))
                    {
                        error = "--ascensions 必须是 0..10 的逗号分隔整数。";
                        return false;
                    }
                    options = options with { Ascensions = ascensions };
                    break;
                case "--resume":
                    options = options with { Resume = true };
                    break;
                case "--force-win":
                    options = options with { ForceWin = true, RandomizeForceWin = false };
                    break;
                case "--force-win-random":
                    options = options with { ForceWin = true, RandomizeForceWin = true };
                    break;
                case "--help" or "-h":
                    error = string.Empty;
                    return false;
                default:
                    error = $"未知参数：{args[index]}";
                    return false;
            }
        }
        return true;
    }

    internal static string Usage => """
        用法：
          dotnet build tools/sts2-sim-export/Sts2SimExport.csproj -c Release -p:NuGetAudit=false
          dotnet artifacts/bin/Sts2SimExport/release/Sts2SimExport.dll --out .local/sts2-sim-export \
              [--runs 300] [--target-combats 1000] [--max-floors 60] [--seed-prefix FS1]
              [--characters IRONCLAD,SILENT,DEFECT,REGENT,NECROBINDER]
              [--ascensions 0,10] [--resume] [--force-win | --force-win-random]

        --force-win 让每场战斗在导出开局后直接按胜利结算，剩余生命保持进入战斗时的值，
        不消耗药水；用于让整局推进到第二、三幕。
        --force-win-random 同样强制胜利，但剩余生命在最大生命的 20%..100% 之间随机
        （不超过进入战斗时的生命），并随机消耗 0 到全部已有药水；随机数由整局种子、
        楼层和战斗序号派生，同样的参数可以重跑出同样的结果。两种强制胜利二选一。
        sts2-sim 路径默认是 .local/sts2-sim，可用 MSBuild 属性 Sts2SimRepo 或环境变量
        STS2_SIM_REPO 指向另一份固定提交的检出。
        """;

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
