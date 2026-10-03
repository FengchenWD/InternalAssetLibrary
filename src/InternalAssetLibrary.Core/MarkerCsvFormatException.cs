namespace InternalAssetLibrary.Core;

public sealed class MarkerCsvFormatException : FormatException
{
    public MarkerCsvFormatException(string message)
        : base(message)
    {
    }
}
