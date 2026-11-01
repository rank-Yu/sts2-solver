using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sts2Solver;

/// <summary>
/// Loads and atomically saves <see cref="SolverSettingsData"/> without touching Godot. Corrupt files
/// are moved aside and the current defaults are used so a bad settings file cannot stop the game.
/// </summary>
internal sealed class SolverSettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Action<string> _info;
    private readonly Action<string> _error;
    private SolverSettingsData _current;

    private SolverSettingsStore(string path, SolverSettingsData current,
        Action<string> info, Action<string> error)
    {
        _path = path;
        _current = current;
        _info = info;
        _error = error;
    }

    internal string Path => _path;

    internal SolverSettingsData Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    internal static SolverSettingsStore Load(string path, Action<string> info, Action<string> error)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            string json = File.ReadAllText(path);
            JsonObject root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("设置文件不是 JSON 对象。");
            int schema = root["schemaVersion"] is JsonValue value
                && value.TryGetValue(out int parsed) ? parsed : 0;
            bool migrated = false;
            SolverSettingsData data;
            if (schema == 1)
            {
                bool fullAuto = root["automation"] is JsonObject automation
                    && automation["autoEnableFullAuto"] is JsonValue autoValue
                    && autoValue.TryGetValue(out bool enabled) && enabled;
                root.Remove("automation");
                root["schemaVersion"] = SolverSettingsData.CurrentSchemaVersion;
                data = JsonSerializer.Deserialize<SolverSettingsData>(root.ToJsonString(), JsonOptions)
                    ?? throw new InvalidDataException("设置文件为空。");
                if (data.Automation is null)
                    throw new InvalidDataException("设置缺少 automation 分区。");
                data = data with
                {
                    Automation = data.Automation with
                    {
                        Mode = fullAuto ? SolverMode.FullAuto : SolverMode.AutoSearch,
                    },
                };
                migrated = true;
            }
            else
            {
                data = JsonSerializer.Deserialize<SolverSettingsData>(json, JsonOptions)
                    ?? throw new InvalidDataException("设置文件为空。");
            }
            data.Validate();
            info($"[Sts2Solver] SETTINGS_LOADED path={path} schema={data.SchemaVersion}");
            if (migrated)
                TryWriteMigrated(path, data, info, error);
            return new SolverSettingsStore(path, data, info, error);
        }
        catch (FileNotFoundException)
        {
            info($"[Sts2Solver] SETTINGS_DEFAULTS reason=missing path={path}");
        }
        catch (DirectoryNotFoundException)
        {
            info($"[Sts2Solver] SETTINGS_DEFAULTS reason=missing path={path}");
        }
        catch (JsonException exception)
        {
            return Corrupt(path, "json", exception.Message, info, error);
        }
        catch (InvalidDataException exception)
        {
            return Corrupt(path, "validation", exception.Message, info, error);
        }
        catch (NotSupportedException exception)
        {
            return Corrupt(path, "unsupported", exception.Message, info, error);
        }
        catch (IOException exception)
        {
            error($"[Sts2Solver] SETTINGS_LOAD_FAILED reason=io path={path} detail={exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            error($"[Sts2Solver] SETTINGS_LOAD_FAILED reason=access path={path} detail={exception.Message}");
        }
        return new SolverSettingsStore(path, new SolverSettingsData(), info, error);
    }

    /// <summary>Validates, writes and then publishes the new settings; failed writes keep the old value.</summary>
    internal bool Update(Func<SolverSettingsData, SolverSettingsData> change, out string? error)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            SolverSettingsData next;
            try
            {
                next = change(_current)
                    ?? throw new InvalidDataException("设置更新返回了空值。");
                next.Validate();
            }
            catch (InvalidDataException exception)
            {
                error = exception.Message;
                _error($"[Sts2Solver] SETTINGS_REJECTED reason=validation path={_path} detail={error}");
                return false;
            }
            try
            {
                string? directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(next, JsonOptions));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (IOException exception)
            {
                error = exception.Message;
                _error($"[Sts2Solver] SETTINGS_SAVE_FAILED reason=io path={_path} detail={error}");
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                error = exception.Message;
                _error($"[Sts2Solver] SETTINGS_SAVE_FAILED reason=access path={_path} detail={error}");
                return false;
            }
            _current = next;
            error = null;
            _info($"[Sts2Solver] SETTINGS_SAVED path={_path} schema={next.SchemaVersion}");
            return true;
        }
    }

    private static void TryWriteMigrated(string path, SolverSettingsData data,
        Action<string> info, Action<string> error)
    {
        try
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temporary, path, overwrite: true);
            info($"[Sts2Solver] SETTINGS_MIGRATED path={path} from=1 to={data.SchemaVersion} " +
                $"mode={data.Automation.Mode}");
        }
        catch (IOException exception)
        {
            error($"[Sts2Solver] SETTINGS_MIGRATION_SAVE_FAILED reason=io path={path} " +
                $"detail={exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            error($"[Sts2Solver] SETTINGS_MIGRATION_SAVE_FAILED reason=access path={path} " +
                $"detail={exception.Message}");
        }
    }

    private static SolverSettingsStore Corrupt(string path, string reason, string detail,
        Action<string> info, Action<string> error)
    {
        string backup = BackupPath(path);
        try
        {
            File.Move(path, backup, overwrite: false);
            error($"[Sts2Solver] SETTINGS_CORRUPT reason={reason} path={path} backup={backup} detail={detail}");
        }
        catch (IOException exception)
        {
            error(
                $"[Sts2Solver] SETTINGS_CORRUPT reason={reason} path={path} backup_failed=io detail={exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            error(
                $"[Sts2Solver] SETTINGS_CORRUPT reason={reason} path={path} backup_failed=access detail={exception.Message}");
        }
        return new SolverSettingsStore(path, new SolverSettingsData(), info, error);
    }

    private static string BackupPath(string path)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        string extension = System.IO.Path.GetExtension(path);
        string baseName = $"{name}.bad-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        string candidate = System.IO.Path.Combine(directory ?? "", baseName + extension);
        for (int suffix = 1; File.Exists(candidate); suffix++)
            candidate = System.IO.Path.Combine(directory ?? "", $"{baseName}-{suffix}{extension}");
        return candidate;
    }
}
