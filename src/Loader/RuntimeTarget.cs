namespace Sts2Solver.Loader;

internal static class RuntimeTarget
{
    // 游戏版本与发布目录的对应关系；正式版与测试版各自维护战斗规则。
    internal static string ForGameVersion(string version) => version.TrimStart('v') switch
    {
        "0.111.1" => "game-public",
        "0.113.1" => "game-public-beta",
        _ => throw new NotSupportedException($"Sts2Solver does not support game {version}."),
    };
}
