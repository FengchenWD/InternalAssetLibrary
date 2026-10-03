namespace InternalAssetLibrary.Contracts;

public sealed record PageRequest(int Page = 1, int PageSize = 50);

public sealed record PageResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount)
{
    public int TotalPages => TotalCount == 0
        ? 0
        : checked((int)((TotalCount + PageSize - 1) / PageSize));
}

public enum SortDirection
{
    Ascending,
    Descending
}
