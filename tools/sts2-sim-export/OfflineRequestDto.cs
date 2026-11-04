using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2SimExport;

/// <summary>
/// The subset of src/TestHost/Combat/OfflineRequest.cs written by this exporter. The fields and
/// camelCase names must stay in sync with that DTO; the differential host rejects unknown members.
/// </summary>
internal sealed record OfflineRequestDto
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static readonly JsonSerializerOptions JsonLineOptions = new(JsonOptions)
    {
        WriteIndented = false,
    };

    public int SchemaVersion { get; init; } = 1;
    public string ScenarioId { get; init; } = string.Empty;
    public string CharacterId { get; init; } = string.Empty;
    public string EncounterId { get; init; } = string.Empty;
    public string Seed { get; init; } = string.Empty;
    public int Ascension { get; init; }
    public int ActIndexForTest { get; init; }
    public string TargetRoomType { get; init; } = "Monster";
    public string TargetMapPointType { get; init; } = "Monster";
    public int EnemyCurrentHp { get; init; } = int.MaxValue;
    public int InitialPlayerHp { get; init; }
    public int InitialPlayerMaxHp { get; init; }
    public ExportCard[] Cards { get; init; } = [];
    public ExportCard[] RunCards { get; init; } = [];
    public ExportRelic[] Relics { get; init; } = [];
    public ExportPotion[] Potions { get; init; } = [];
    public bool ClearRunDeck { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 120;
}

internal sealed record ExportCard
{
    public string CardId { get; init; } = string.Empty;
    public int Count { get; init; } = 1;
    public int UpgradeLevels { get; init; }
    public string? EnchantmentId { get; init; }
    public int EnchantmentAmount { get; init; } = 1;
    public string? AfflictionId { get; init; }
    public int AfflictionAmount { get; init; } = 1;
    public Dictionary<string, int>? DynamicVars { get; init; }
    public Dictionary<string, string>? EnumMembers { get; init; }
}

internal sealed record ExportRelic
{
    public string RelicId { get; init; } = string.Empty;
    public bool AddWithoutObtainedEffects { get; init; } = true;
    public Dictionary<string, int>? IntegerMembers { get; init; }
    public Dictionary<string, bool>? BooleanMembers { get; init; }
}

internal sealed record ExportPotion
{
    public string PotionId { get; init; } = string.Empty;
}
