namespace InternalAssetLibrary.Contracts;

public sealed record TagSummary(
    Guid Id,
    string Name,
    string NormalizedName,
    DateTimeOffset CreatedAt,
    Guid CreatedByUserId);

public sealed record CreateTagRequest(string Name);

public sealed record RenameTagRequest(string Name);
