using System.Globalization;
using System.Text;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Core;

public static class MarkersCsv
{
    public const string Header = "标记序号,标记时间,备注,录像名称,录像路径,录制开始,录制时长,标记总数,标记名称";

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Write(MarkerCsvDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDocument(document);

        var text = new StringBuilder(Header.Length + document.Markers.Count * 96);
        text.Append(Header).Append("\r\n");

        if (document.Markers.Count == 0)
        {
            text.Append("1,,,,,,,,\r\n");
        }
        else
        {
            for (var index = 0; index < document.Markers.Count; index++)
            {
                var marker = document.Markers[index];
                var first = index == 0;
                AppendRecord(text,
                [
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    FormatMarkerTime(marker.Time),
                    marker.Note ?? string.Empty,
                    first ? document.RecordingName ?? string.Empty : string.Empty,
                    first ? document.RecordingPath ?? string.Empty : string.Empty,
                    first && document.RecordingStartedAt is not null
                        ? document.RecordingStartedAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        : string.Empty,
                    first && document.RecordingDuration is not null
                        ? FormatDuration(document.RecordingDuration.Value)
                        : string.Empty,
                    first ? document.Markers.Count.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    marker.Name ?? string.Empty
                ]);
            }
        }

        var body = StrictUtf8.GetBytes(text.ToString());
        var result = new byte[Utf8Bom.Length + body.Length];
        Utf8Bom.CopyTo(result, 0);
        body.CopyTo(result, Utf8Bom.Length);
        return result;
    }

    public static MarkerCsvDocument Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Utf8Bom.Length || !bytes[..Utf8Bom.Length].SequenceEqual(Utf8Bom))
        {
            throw new MarkerCsvFormatException("Markers CSV must start with a UTF-8 BOM.");
        }

        if (bytes.Length < Utf8Bom.Length + 2 || bytes[^2] != '\r' || bytes[^1] != '\n')
        {
            throw new MarkerCsvFormatException("Markers CSV must end with CRLF.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes[Utf8Bom.Length..]);
        }
        catch (DecoderFallbackException exception)
        {
            throw new MarkerCsvFormatException($"Markers CSV is not valid UTF-8: {exception.Message}");
        }

        var rows = ParseRecords(text);
        if (rows.Count < 2)
        {
            throw new MarkerCsvFormatException("Markers CSV must contain the header and at least one data row.");
        }

        var expectedHeader = Header.Split(',');
        if (!rows[0].SequenceEqual(expectedHeader, StringComparer.Ordinal))
        {
            throw new MarkerCsvFormatException("Markers CSV header does not match the fixed nine-column format.");
        }

        var dataRows = rows.Skip(1).ToArray();
        for (var index = 0; index < dataRows.Length; index++)
        {
            if (dataRows[index].Length != expectedHeader.Length)
            {
                throw new MarkerCsvFormatException($"Data row {index + 1} must contain exactly nine columns.");
            }
        }

        if (IsEmptyPlaceholder(dataRows))
        {
            return new MarkerCsvDocument(null, null, null, null, Array.Empty<MarkerCsvEntry>());
        }

        if (dataRows.Any(row => string.IsNullOrEmpty(row[1])))
        {
            throw new MarkerCsvFormatException("Only a zero-marker document may contain an empty marker time.");
        }

        string? recordingName = NullIfEmpty(dataRows[0][3]);
        string? recordingPath = NullIfEmpty(dataRows[0][4]);
        DateTime? recordingStartedAt = ParseRecordingStart(dataRows[0][5]);
        TimeSpan? recordingDuration = ParseOptionalDuration(dataRows[0][6]);

        if (!int.TryParse(dataRows[0][7], NumberStyles.None, CultureInfo.InvariantCulture, out var declaredCount) ||
            declaredCount != dataRows.Length)
        {
            throw new MarkerCsvFormatException("The first row marker count must equal the number of marker rows.");
        }

        var markers = new MarkerCsvEntry[dataRows.Length];
        TimeSpan? previousTime = null;

        for (var index = 0; index < dataRows.Length; index++)
        {
            var row = dataRows[index];
            if (!int.TryParse(row[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence != index + 1)
            {
                throw new MarkerCsvFormatException("Marker sequence numbers must start at one and be contiguous.");
            }

            if (index > 0 && row.Skip(3).Take(5).Any(value => value.Length != 0))
            {
                throw new MarkerCsvFormatException("Recording metadata may only appear in the first marker row.");
            }

            var time = ParseMarkerTime(row[1]);
            if (previousTime is not null && time < previousTime.Value)
            {
                throw new MarkerCsvFormatException("Markers must be ordered by time.");
            }

            if (recordingDuration is not null && time > recordingDuration.Value)
            {
                throw new MarkerCsvFormatException("A marker cannot be later than the recording duration.");
            }

            markers[index] = new MarkerCsvEntry(time, NullIfEmpty(row[8]), NullIfEmpty(row[2]));
            previousTime = time;
        }

        return new MarkerCsvDocument(
            recordingName,
            recordingPath,
            recordingStartedAt,
            recordingDuration,
            Array.AsReadOnly(markers));
    }

    public static MarkerCsvDocument Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    public static void Write(Stream destination, MarkerCsvDocument document)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Write(document));
    }

    public static string FormatMarkerTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Marker time cannot be negative.");
        }

        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException("Marker time must have millisecond precision.", nameof(value));
        }

        var totalHours = value.Ticks / TimeSpan.TicksPerHour;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}");
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Recording duration cannot be negative.");
        }

        if (value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("Recording duration must have whole-second precision.", nameof(value));
        }

        var totalHours = value.Ticks / TimeSpan.TicksPerHour;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}");
    }

    private static void ValidateDocument(MarkerCsvDocument document)
    {
        ArgumentNullException.ThrowIfNull(document.Markers);
        if (document.RecordingDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(document), "Recording duration cannot be negative.");
        }

        if (document.RecordingDuration is not null &&
            document.RecordingDuration.Value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("Recording duration must have whole-second precision.", nameof(document));
        }

        TimeSpan? previous = null;
        foreach (var marker in document.Markers)
        {
            if (marker is null)
            {
                throw new ArgumentException("Markers cannot contain null items.", nameof(document));
            }

            if (marker.Time < TimeSpan.Zero)
            {
                throw new ArgumentException("Marker time cannot be negative.", nameof(document));
            }

            if (marker.Time.Ticks % TimeSpan.TicksPerMillisecond != 0)
            {
                throw new ArgumentException("Marker time must have millisecond precision.", nameof(document));
            }

            if (previous is not null && marker.Time < previous.Value)
            {
                throw new ArgumentException("Markers must be ordered by time.", nameof(document));
            }

            if (document.RecordingDuration is not null && marker.Time > document.RecordingDuration.Value)
            {
                throw new ArgumentException("A marker cannot be later than the recording duration.", nameof(document));
            }

            previous = marker.Time;
        }
    }

    private static void AppendRecord(StringBuilder output, IReadOnlyList<string> fields)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (index > 0)
            {
                output.Append(',');
            }

            AppendField(output, fields[index]);
        }

        output.Append("\r\n");
    }

    private static void AppendField(StringBuilder output, string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            output.Append(value);
            return;
        }

        output.Append('"');
        foreach (var character in value)
        {
            if (character == '"')
            {
                output.Append("\"\"");
            }
            else
            {
                output.Append(character);
            }
        }

        output.Append('"');
    }

    private static List<string[]> ParseRecords(string text)
    {
        var records = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var afterQuote = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                        afterQuote = true;
                    }
                }
                else if (character == '\r')
                {
                    RequireLineFeed(text, index);
                    field.Append("\r\n");
                    index++;
                }
                else if (character == '\n')
                {
                    throw new MarkerCsvFormatException("RFC 4180 fields must use CRLF line endings.");
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            if (afterQuote)
            {
                if (character == ',')
                {
                    EndField(row, field);
                    afterQuote = false;
                    continue;
                }

                if (character == '\r')
                {
                    RequireLineFeed(text, index);
                    EndRecord(records, row, field);
                    afterQuote = false;
                    index++;
                    continue;
                }

                throw new MarkerCsvFormatException("A quoted CSV field must be followed by a comma or CRLF.");
            }

            if (character == '"')
            {
                if (field.Length != 0)
                {
                    throw new MarkerCsvFormatException("A quote may only begin at the start of a CSV field.");
                }

                quoted = true;
            }
            else if (character == ',')
            {
                EndField(row, field);
            }
            else if (character == '\r')
            {
                RequireLineFeed(text, index);
                EndRecord(records, row, field);
                index++;
            }
            else if (character == '\n')
            {
                throw new MarkerCsvFormatException("RFC 4180 records must use CRLF line endings.");
            }
            else
            {
                field.Append(character);
            }
        }

        if (quoted)
        {
            throw new MarkerCsvFormatException("Quoted CSV field is not closed.");
        }

        if (afterQuote || row.Count > 0 || field.Length > 0)
        {
            EndRecord(records, row, field);
        }

        return records;
    }

    private static void RequireLineFeed(string text, int carriageReturnIndex)
    {
        if (carriageReturnIndex + 1 >= text.Length || text[carriageReturnIndex + 1] != '\n')
        {
            throw new MarkerCsvFormatException("RFC 4180 records must use CRLF line endings.");
        }
    }

    private static void EndField(List<string> row, StringBuilder field)
    {
        row.Add(field.ToString());
        field.Clear();
    }

    private static void EndRecord(List<string[]> records, List<string> row, StringBuilder field)
    {
        EndField(row, field);
        records.Add(row.ToArray());
        row.Clear();
    }

    private static bool IsEmptyPlaceholder(IReadOnlyList<string[]> rows) =>
        rows.Count == 1 && rows[0][0] == "1" && rows[0].Skip(1).All(value => value.Length == 0);

    private static DateTime? ParseRecordingStart(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var result))
        {
            throw new MarkerCsvFormatException("Recording start must use yyyy-MM-dd HH:mm:ss.");
        }

        return result;
    }

    private static TimeSpan ParseMarkerTime(string value) => ParseClock(value, includeMilliseconds: true, "marker time");

    private static TimeSpan? ParseOptionalDuration(string value) =>
        value.Length == 0 ? null : ParseClock(value, includeMilliseconds: false, "recording duration");

    private static TimeSpan ParseClock(string value, bool includeMilliseconds, string fieldName)
    {
        var dotIndex = value.IndexOf('.');
        if (includeMilliseconds)
        {
            if (dotIndex < 0 || value.Length - dotIndex - 1 != 3)
            {
                throw new MarkerCsvFormatException($"The {fieldName} must use HH:mm:ss.fff.");
            }
        }
        else if (dotIndex >= 0)
        {
            throw new MarkerCsvFormatException($"The {fieldName} must use HH:mm:ss.");
        }

        var clock = includeMilliseconds ? value[..dotIndex] : value;
        var parts = clock.Split(':');
        if (parts.Length != 3 || parts[0].Length < 2 || parts[1].Length != 2 || parts[2].Length != 2 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            minutes is < 0 or > 59 || seconds is < 0 or > 59)
        {
            throw new MarkerCsvFormatException($"The {fieldName} has an invalid clock value.");
        }

        var milliseconds = 0;
        if (includeMilliseconds &&
            !int.TryParse(value[(dotIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds))
        {
            throw new MarkerCsvFormatException($"The {fieldName} has invalid milliseconds.");
        }

        try
        {
            var ticks = checked(hours * TimeSpan.TicksPerHour);
            ticks = checked(ticks + minutes * TimeSpan.TicksPerMinute);
            ticks = checked(ticks + seconds * TimeSpan.TicksPerSecond);
            ticks = checked(ticks + milliseconds * TimeSpan.TicksPerMillisecond);
            return new TimeSpan(ticks);
        }
        catch (OverflowException)
        {
            throw new MarkerCsvFormatException($"The {fieldName} is outside the supported range.");
        }
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
