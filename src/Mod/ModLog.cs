using MegaCrit.Sts2.Core.Logging;

namespace Sts2Solver;

/// <summary>Sts2Solver 的日志门面：同时写游戏日志与测试目录下的 mod.log。</summary>
internal static class ModLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string? path)
    {
        _path = path;
        if (path == null)
            return;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, "");
    }

    public static void Info(string message) => Write("INFO", message, message => Log.Info(message));

    public static void Warn(string message) => Write("WARN", message, message => Log.Warn(message));

    public static void Error(string message) => Write("ERROR", message, message => Log.Error(message));

    private static void Write(string level, string message, Action<string> gameLog)
    {
        gameLog(message);
        CombatLogRecorder.AppendLine(level, message);
        if (_path == null)
            return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path, $"{DateTime.UtcNow:O} {level} {message}\n");
            }
        }
        catch (IOException)
        {
            // 游戏日志已经写出；测试日志失败不应影响捕获。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
