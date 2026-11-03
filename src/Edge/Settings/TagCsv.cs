using System.Globalization;
using System.Text;
using System.Text.Json;
using IndustrialIot.Edge.Configuration;
using IndustrialIot.Edge.Drivers;
using Microsoft.AspNetCore.Http;

namespace IndustrialIot.Edge.Settings;

/// <summary>One refused CSV cell: the line its record starts on (the header is line 1), its column, a fixed message.</summary>
public sealed record CsvError(int Line, string Column, string Message);

/// <summary>The tags of a CSV file, or (with <see cref="Tags"/> null) why it was refused.</summary>
public sealed record CsvParseResult(IReadOnlyList<DeviceTagOptions>? Tags, IReadOnlyList<CsvError> Errors);

/// <summary>
/// One PLC's tags as CSV for a spreadsheet, behind <c>POST /api/settings/csv/format</c> and
/// <c>POST /api/settings/csv/parse</c> (the request guard, no password: nothing is saved). Comma-separated, RFC 4180
/// quotes, UTF-8 with or without BOM, CRLF or LF, invariant numbers; an empty cell is "not set" and cells are not
/// trimmed. Parse checks the file and the cell types only; the PLC rules are the save's. A text cell that Excel would
/// run as a formula is written with a leading <c>'</c>, which parse removes again.
/// </summary>
public static class TagCsv
{
    public static readonly IReadOnlyList<string> Columns =
        ["Tag Name", "Address", "Data Type", "Client Access", "Description", "Min", "Max", "Length"];

    private const string ReadOnly = "Read Only";
    private const string ReadWrite = "Read/Write";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Format(IEnumerable<DeviceTagOptions> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var csv = new StringBuilder();
        AppendRecord(csv, Columns);
        foreach (var tag in tags)
        {
            AppendRecord(csv,
            [
                Guard(tag.Name),
                Guard(tag.Address),
                Guard(tag.DataType),
                tag.Writable ? ReadWrite : ReadOnly,
                Guard(tag.Description),
                tag.Min?.ToString(CultureInfo.InvariantCulture),
                tag.Max?.ToString(CultureInfo.InvariantCulture),
                tag.Length?.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        return csv.ToString();
    }

    public static CsvParseResult Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var errors = new List<CsvError>();
        var records = Records(csv.StartsWith('\uFEFF') ? csv[1..] : csv, errors);
        if (errors.Count > 0)
        {
            return new CsvParseResult(null, errors);
        }

        var header = records.FirstOrDefault();
        var mismatch = header is null ? 0 : Enumerable.Range(0, Columns.Count)
            .FirstOrDefault(i => i >= header.Cells.Count || !header.Cells[i].Trim().Equals(Columns[i], StringComparison.OrdinalIgnoreCase), -1);
        if (mismatch >= 0 || header!.Cells.Count != Columns.Count)
        {
            var column = Columns[mismatch >= 0 ? mismatch : Columns.Count - 1];
            return new CsvParseResult(null, [new CsvError(header?.Line ?? 1, column, $"The first line must be the header: {string.Join(", ", Columns)}.")]);
        }

        var tags = new List<DeviceTagOptions>();
        foreach (var record in records.Skip(1))
        {
            if (Tag(record, errors) is { } tag)
            {
                tags.Add(tag);
            }
        }

        return errors.Count > 0 ? new CsvParseResult(null, errors) : new CsvParseResult(tags, []);
    }

    /// <summary><c>POST /api/settings/csv/format {tags}</c> → <c>{csv}</c>.</summary>
    public static async Task FormatAsync(HttpContext context, int monitorPort)
    {
        using var body = await SettingsService.ReadGuardedAsync(context, monitorPort, SettingsService.MaxBodyBytes).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        DeviceTagOptions[]? tags = null;
        try
        {
            if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("tags", out var given))
            {
                tags = given.Deserialize<DeviceTagOptions[]>(Json);
            }
        }
        catch (JsonException)
        {
            tags = null;
        }

        if (tags is null || tags.Any(tag => tag is null))
        {
            await SettingsService.Reply(context, StatusCodes.Status400BadRequest,
                new { ok = false, errors = new[] { new SettingsError("tags", "Required, as a list of tags.") } }).ConfigureAwait(false);
            return;
        }

        await SettingsService.Reply(context, StatusCodes.Status200OK, new { csv = Format(tags) }).ConfigureAwait(false);
    }

    /// <summary><c>POST /api/settings/csv/parse {csv}</c> → <c>{tags}</c>, or 400 with the errors by line and column.</summary>
    public static async Task ParseAsync(HttpContext context, int monitorPort)
    {
        using var body = await SettingsService.ReadGuardedAsync(context, monitorPort, SettingsService.MaxBodyBytes).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        if (body.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("csv", out var csv)
            || csv.ValueKind != JsonValueKind.String)
        {
            await SettingsService.Reply(context, StatusCodes.Status400BadRequest,
                new { ok = false, errors = new[] { new CsvError(0, "", "Required: the file's text as csv.") } }).ConfigureAwait(false);
            return;
        }

        var result = Parse(csv.GetString()!);
        await (result.Tags is { } tags
            ? SettingsService.Reply(context, StatusCodes.Status200OK, new { ok = true, tags })
            : SettingsService.Reply(context, StatusCodes.Status400BadRequest, new { ok = false, errors = result.Errors })).ConfigureAwait(false);
    }

    private static DeviceTagOptions? Tag(Record record, List<CsvError> errors)
    {
        var line = record.Line;
        var before = errors.Count;
        if (record.Cells.Count != Columns.Count)
        {
            var column = Columns[Math.Min(record.Cells.Count, Columns.Count - 1)];
            errors.Add(new CsvError(line, column, $"A tag needs {Columns.Count} cells."));
            return null;
        }

        var cells = record.Cells;
        var name = Unguard(cells[0]);
        if (name.Length == 0)
        {
            errors.Add(new CsvError(line, Columns[0], "Required."));
        }

        var dataType = Unguard(cells[2]);
        if (!Enum.GetNames<PlcDataType>().Contains(dataType, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new CsvError(line, Columns[2], $"Must be one of {string.Join(", ", Enum.GetNames<PlcDataType>())}."));
        }

        bool writable = false;
        if (cells[3].Equals(ReadWrite, StringComparison.OrdinalIgnoreCase))
        {
            writable = true;
        }
        else if (!cells[3].Equals(ReadOnly, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new CsvError(line, Columns[3], $"Must be {ReadOnly} or {ReadWrite}."));
        }

        var min = Number(cells[5], line, Columns[5], errors);
        var max = Number(cells[6], line, Columns[6], errors);
        int? length = null;
        if (cells[7].Length > 0)
        {
            if (int.TryParse(cells[7], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                length = value;
            }
            else
            {
                errors.Add(new CsvError(line, Columns[7], "Must be a whole number of characters."));
            }
        }

        if (errors.Count > before)
        {
            return null;
        }

        var description = Unguard(cells[4]);
        return new DeviceTagOptions
        {
            Name = name,
            Address = Unguard(cells[1]),
            DataType = dataType,
            Writable = writable,
            Description = description.Length == 0 ? null : description,
            Min = min,
            Max = max,
            Length = length,
        };
    }

    private static decimal? Number(string cell, int line, string column, List<CsvError> errors)
    {
        if (cell.Length == 0)
        {
            return null;
        }

        if (decimal.TryParse(cell, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        errors.Add(new CsvError(line, column, "Must be a number, with . as the decimal point."));
        return null;
    }

    /// <summary>
    /// A text cell Excel would run as a formula (<c>= + - @</c>, tab, CR first) gets a leading <c>'</c>; so does one
    /// that starts with <c>'</c> before such a character or another <c>'</c>, so that <see cref="Unguard"/> gives back
    /// exactly the text written.
    /// </summary>
    private static string Guard(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return IsFormulaStart(text[0]) || (text[0] == '\'' && text.Length > 1 && (IsFormulaStart(text[1]) || text[1] == '\''))
            ? "'" + text
            : text;
    }

    private static string Unguard(string cell) =>
        cell.Length > 1 && cell[0] == '\'' && (IsFormulaStart(cell[1]) || cell[1] == '\'') ? cell[1..] : cell;

    private static bool IsFormulaStart(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    private static void AppendRecord(StringBuilder csv, IReadOnlyList<string?> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                csv.Append(',');
            }

            var cell = cells[i] ?? "";
            if (cell.AsSpan().IndexOfAny(",\"\r\n") >= 0 || (cell.Length > 0 && (cell[0] == ' ' || cell[^1] == ' ')))
            {
                csv.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                csv.Append(cell);
            }
        }

        csv.Append("\r\n");
    }

    private sealed record Record(int Line, List<string> Cells);

    /// <summary>
    /// RFC 4180 records with the physical line each starts on; blank lines are skipped. A quote inside an unquoted cell,
    /// text after a closing quote, or a quote never closed refuses the file at that cell.
    /// </summary>
    private static List<Record> Records(string csv, List<CsvError> errors)
    {
        var records = new List<Record>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var line = 1;
        var start = 1;
        var quoted = false;
        var closed = false;

        void EndCell()
        {
            cells.Add(cell.ToString());
            cell.Clear();
            closed = false;
        }

        void EndRecord()
        {
            EndCell();
            if (cells.Count > 1 || cells[0].Length > 0)
            {
                records.Add(new Record(start, [.. cells]));
            }

            cells.Clear();
        }

        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                    closed = true;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    cell.Append(c);
                }

                continue;
            }

            if (c == ',')
            {
                EndCell();
            }
            else if (c == '\n' || (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n'))
            {
                if (c == '\r')
                {
                    i++;
                }

                EndRecord();
                line++;
                start = line;
            }
            else if (c == '"' && cell.Length == 0 && !closed)
            {
                quoted = true;
            }
            else if (c == '"' || closed)
            {
                errors.Add(new CsvError(start, ColumnAt(cells.Count), "A quote is not valid here; quote the whole cell and double the quotes inside."));
                return [];
            }
            else
            {
                cell.Append(c);
            }
        }

        if (quoted)
        {
            errors.Add(new CsvError(start, ColumnAt(cells.Count), "A quoted cell is never closed."));
            return [];
        }

        if (cell.Length > 0 || cells.Count > 0 || closed)
        {
            EndRecord();
        }

        return records;
    }

    private static string ColumnAt(int index) => Columns[Math.Min(index, Columns.Count - 1)];
}
