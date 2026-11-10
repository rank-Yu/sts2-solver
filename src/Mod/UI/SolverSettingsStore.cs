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

    /// <summary>
    /// Upgrades schema 6 and 7 files to the current schema. Schema 6 stored the three-position
    /// automation mode; later schemas keep only the auto-search switch: Manual becomes off, AutoSearch
    /// and FullAuto become on (executing the whole combat is no longer persisted). Schemas 6 and 7
    /// could force a potion: those directives are dropped and the force-all preset becomes Smart.
    /// Every other value is kept as written; other versions go through the normal checks.
    /// </summary>
    private static string UpgradeSchema(string json, Action<string> info)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return json;
        }
        if (root is not JsonObject settings
            || settings["schemaVersion"] is not JsonValue version
            || !version.TryGetValue(out int schema) || schema is not (6 or 7))
        {
            return json;
        }
        settings["schemaVersion"] = SolverSettingsData.CurrentSchemaVersion;
        if (schema == 6 && settings["automation"] is JsonObject automation
            && automation["mode"] is JsonValue mode)
        {
            string? value = mode.TryGetValue(out string? text) ? text : null;
            automation.Remove("mode");
            automation["autoSearch"] = !string.Equals(value, "Manual", StringComparison.OrdinalIgnoreCase);
        }
        if (settings["search"] is JsonObject search && search["potions"] is JsonObject potions)
        {
            if (potions["directives"] is JsonArray directives)
            {
                for (int index = directives.Count - 1; index >= 0; index--)
                {
                    if (directives[index] is JsonObject entry && IsText(entry["directive"], "Force"))
                        directives.RemoveAt(index);
                }
            }
            if (IsText(potions["preset"], "ForceAll"))
                potions["preset"] = nameof(SolverPotionPreset.Smart);
        }
        info($"[Sts2Solver] SETTINGS_UPGRADED from={schema} to={SolverSettingsData.CurrentSchemaVersion}");
        return settings.ToJsonString();

        static bool IsText(JsonNode? node, string expected)
            => node is JsonValue value && value.TryGetValue(out string? text)
                && string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static SolverSettingsStore Load(string path, Action<string> info, Action<string> error)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            string json = UpgradeSchema(File.ReadAllText(path), info);
            SolverSettingsData data = JsonSerializer.Deserialize<SolverSettingsData>(json, JsonOptions)
                ?? throw new InvalidDataException("设置文件为空。");
            data.Validate();
            info($"[Sts2Solver] SETTINGS_LOADED path={path} schema={data.SchemaVersion}");
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
            SolverSettingsData previous = _current;
            _current = next;
            error = null;
            _info($"[Sts2Solver] SETTINGS_SAVED path={_path} schema={next.SchemaVersion} "
                + $"changed={DescribeChanges(previous, next)}");
            return true;
        }
    }

    /// <summary>
    /// The changed settings as <c>path:old-&gt;new</c> pairs, so a log shows what a save changed (for
    /// example <c>search.tier:ExtraHigh-&gt;Medium</c>). Arrays compare as one value.
    /// </summary>
    internal static string DescribeChanges(SolverSettingsData previous, SolverSettingsData next)
    {
        List<string> changes = [];
        Compare(JsonSerializer.SerializeToNode(previous, JsonOptions),
            JsonSerializer.SerializeToNode(next, JsonOptions), string.Empty, changes);
        return changes.Count == 0 ? "-" : string.Join(",", changes);

        static void Compare(JsonNode? before, JsonNode? after, string path, List<string> changes)
        {
            if (before is JsonObject left && after is JsonObject right)
            {
                foreach (string key in left.Select(static pair => pair.Key)
                    .Union(right.Select(static pair => pair.Key), StringComparer.Ordinal))
                {
                    Compare(left[key], right[key], path.Length == 0 ? key : path + "." + key, changes);
                }
                return;
            }
            string old = before?.ToJsonString() ?? "null";
            string now = after?.ToJsonString() ?? "null";
            if (old != now)
                changes.Add($"{path}:{Compact(old)}->{Compact(now)}");
        }

        static string Compact(string json)
            => json.Replace("\"", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
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
