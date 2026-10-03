using System.Text.Json.Serialization;

namespace InternalAssetLibrary.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<TeamLutState>))]
public enum TeamLutState
{
    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("recycled")]
    Recycled
}

public sealed record TeamLutUploader(Guid Id, string Username, string DisplayName);

public sealed record TeamLutSummary(
    Guid Id,
    Guid CurrentVersionId,
    string Name,
    string OriginalFileName,
    string? Note,
    long SizeBytes,
    string Sha256,
    bool HasContent,
    int Version,
    TeamLutState State,
    TeamLutUploader UploadedBy,
    DateTimeOffset UploadedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RecycledAt,
    DateTimeOffset? PurgeAfter);

public sealed record CreateTeamLutRequest(
    string Name,
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    string? Note = null);

public sealed record UpdateTeamLutRequest(string Name, string? Note = null);
