using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using FileOrbit.Core.Capabilities;

namespace FileOrbit.Engines.Data;

/// <summary>A sheet of cell values (string, double, bool, DateTime or null).</summary>
internal sealed record Table(string Name, List<List<object?>> Rows)
{
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
}

/// <summary>
/// Built-in conversion between CSV, TSV, XLSX, JSON and HTML tables. Uses ClosedXML (MIT) for
/// Excel workbooks. Formulas are exported as their last calculated values.
/// </summary>
public sealed class TabularEngine : ConversionEngineBase
{
    public const string EngineId = "tabular";
    public const long MaxInputBytes = 256L * 1024 * 1024;
    private const int ExcelMaxRows = 1_048_576;
    private const int ExcelMaxColumns = 16_384;

    public override string Id => EngineId;

    public override string DisplayName => "Tabular data (ClosedXML)";

    public override string Description => "Built-in conversion between Excel workbooks, CSV, TSV, JSON and HTML tables.";

    protected override EngineStatus CheckStatus() => EngineStatus.Available("Ready (built in)");

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        var sources = new[] { "xlsx", "csv", "tsv", "json" };
        var targets = new[] { "xlsx", "csv", "tsv", "json", "html" };
        foreach (var source in sources)
        {
            foreach (var target in targets.Where(t => t != source))
            {
                var fromWorkbook = source == "xlsx";
                yield return Cap(source, target, FidelityFor(source, target), quality: 70) with
                {
                    Shape = fromWorkbook && target is "csv" or "tsv" or "json" ? OutputShape.FileOrFolder : OutputShape.File,
                    Note = NoteFor(source, target),
                    Options = OptionsFor(source, target),
                };
            }
        }
    }

    public override Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        if (context.Input.SizeBytes > MaxInputBytes)
        {
            throw new ConversionException("The file is too large for spreadsheet conversion (limit 256 MB).");
        }

        return Task.Run(() => Convert(context, cancellationToken), cancellationToken);
    }

    private static void Convert(ConversionContext context, CancellationToken cancellationToken)
    {
        context.Report(0.05, "Reading data");
        var options = context.Options;
        var tables = context.Input.Format.Id switch
        {
            "xlsx" => ReadWorkbook(context.Input.Path, options.Get("sheets", "first") == "all", context),
            "csv" or "tsv" => [ReadDelimited(context.Input.Path, context.Input.Format.Id == "tsv" ? '\t' : null)],
            "json" => [ReadJson(context.Input.Path)],
            _ => throw new ConversionException($"Cannot read {context.Input.Format.Name} as a table."),
        };
        cancellationToken.ThrowIfCancellationRequested();

        context.Report(0.6, "Writing output");
        var target = context.Target.Id;
        var hasHeader = options.GetBool("header", true);
        switch (target)
        {
            case "xlsx":
                WriteWorkbook(tables, context.PrimaryOutputPath, inferTypes: context.Input.Format.Id != "json" && options.GetBool("inferTypes", true));
                break;
            case "html":
                WriteHtml(tables, context.PrimaryOutputPath, hasHeader, Path.GetFileNameWithoutExtension(context.Input.Path));
                break;
            default:
                foreach (var table in tables)
                {
                    var path = tables.Count == 1
                        ? context.PrimaryOutputPath
                        : context.OutputPath($"{context.OutputBaseName} - {Core.Planning.PathSafety.SanitizeFileName(table.Name, "sheet")}{context.Target.PrimaryExtension}");
                    if (target == "json")
                    {
                        WriteJson(table, path, hasHeader);
                    }
                    else
                    {
                        var delimiter = target == "tsv" ? '\t' : options.Get("delimiter", ",")[0];
                        WriteDelimited(table, path, delimiter);
                    }
                }

                break;
        }

        context.Report(1, "Done");
    }

    internal static List<Table> ReadWorkbook(string path, bool allSheets, ConversionContext? context = null)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(path);
        }
        catch (Exception ex) when (ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase) || ex is FileFormatException)
        {
            throw new ConversionException("The workbook is encrypted or not a valid XLSX file. Password-protected workbooks need Microsoft Excel.", ex.Message, ex);
        }
        catch (Exception ex) when (ex is not ConversionException)
        {
            throw new ConversionException("The workbook could not be read.", ex.Message, ex);
        }

        using (workbook)
        {
            var sheets = workbook.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible).ToList();
            if (sheets.Count == 0)
            {
                throw new ConversionException("The workbook has no visible worksheets.");
            }

            if (!allSheets && sheets.Count > 1)
            {
                context?.AddWarning($"The workbook has {sheets.Count} sheets; only \"{sheets[0].Name}\" was converted. Choose \"All sheets\" to export every sheet.");
                sheets = [sheets[0]];
            }

            var tables = new List<Table>();
            foreach (var sheet in sheets)
            {
                var rows = new List<List<object?>>();
                var used = sheet.RangeUsed(XLCellsUsedOptions.Contents);
                if (used is not null)
                {
                    var firstColumn = used.FirstColumn().ColumnNumber();
                    var lastColumn = used.LastColumn().ColumnNumber();
                    for (var r = used.FirstRow().RowNumber(); r <= used.LastRow().RowNumber(); r++)
                    {
                        var row = new List<object?>();
                        for (var c = firstColumn; c <= lastColumn; c++)
                        {
                            row.Add(CellValue(sheet.Cell(r, c)));
                        }

                        rows.Add(row);
                    }
                }

                tables.Add(new Table(sheet.Name, rows));
            }

            return tables;
        }
    }

    private static object? CellValue(IXLCell cell)
    {
        XLCellValue value;
        try
        {
            // Prefer the value Excel last calculated; evaluate only when none was saved (e.g. generated files).
            value = cell.HasFormula ? cell.CachedValue : cell.Value;
            if (cell.HasFormula && value.Type == XLDataType.Blank)
            {
                value = cell.Value;
            }
        }
        catch (Exception)
        {
            return cell.GetFormattedString();
        }

        return value.Type switch
        {
            XLDataType.Blank => null,
            XLDataType.Boolean => value.GetBoolean(),
            XLDataType.Number => value.GetNumber(),
            XLDataType.DateTime => value.GetDateTime(),
            XLDataType.TimeSpan => value.GetTimeSpan().ToString(),
            XLDataType.Error => "#" + value.GetError().ToString().ToUpperInvariant(),
            _ => value.GetText(),
        };
    }

    internal static Table ReadDelimited(string path, char? delimiter)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sample = new char[16 * 1024];
        var sampleLength = reader.ReadBlock(sample, 0, sample.Length);
        var actualDelimiter = delimiter ?? CsvFormat.DetectDelimiter(new string(sample, 0, sampleLength));

        reader.BaseStream.Seek(0, SeekOrigin.Begin);
        reader.DiscardBufferedData();
        var rows = CsvFormat.Read(reader, actualDelimiter, ExcelMaxRows + 1);
        return new Table(Path.GetFileNameWithoutExtension(path), rows.Select(r => r.Select(v => (object?)v).ToList()).ToList());
    }

    internal static Table ReadJson(string path)
    {
        JsonNode? root;
        try
        {
            using var stream = File.OpenRead(path);
            root = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new ConversionException("The file is not valid JSON.", ex.Message, ex);
        }

        // Accept an array, or an object wrapping a single array (e.g. {"data": [...]}), or a single object.
        if (root is JsonObject wrapper && wrapper.Count == 1 && wrapper.First().Value is JsonArray inner)
        {
            root = inner;
        }

        var items = root switch
        {
            JsonArray array => array.ToList(),
            JsonObject obj => [obj],
            _ => throw new ConversionException("The JSON must contain an array of records to convert to a table."),
        };

        if (items.All(i => i is JsonArray))
        {
            return new Table("Data", items.Select(i => ((JsonArray)i!).Select(JsonScalar).ToList()).ToList());
        }

        var columns = new List<string>();
        foreach (var item in items.OfType<JsonObject>())
        {
            foreach (var property in item)
            {
                if (!columns.Contains(property.Key))
                {
                    columns.Add(property.Key);
                }
            }
        }

        if (columns.Count == 0)
        {
            throw new ConversionException("The JSON array does not contain records (objects or arrays).");
        }

        var rows = new List<List<object?>> { columns.Select(c => (object?)c).ToList() };
        foreach (var item in items)
        {
            if (item is JsonObject record)
            {
                rows.Add(columns.Select(c => record.TryGetPropertyValue(c, out var v) ? JsonScalar(v) : null).ToList());
            }
            else
            {
                rows.Add([JsonScalar(item)]);
            }
        }

        return new Table("Data", rows);
    }

    private static object? JsonScalar(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<bool>(out var b) => b,
        JsonValue value when value.TryGetValue<double>(out var d) => d,
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(), // Nested objects/arrays are kept as JSON text.
    };

    internal static void WriteWorkbook(IReadOnlyList<Table> tables, string path, bool inferTypes)
    {
        using var workbook = new XLWorkbook();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            if (table.Rows.Count > ExcelMaxRows || table.ColumnCount > ExcelMaxColumns)
            {
                throw new ConversionException($"The data has more rows or columns than Excel supports ({ExcelMaxRows:N0} rows, {ExcelMaxColumns:N0} columns).");
            }

            var sheet = workbook.Worksheets.Add(UniqueSheetName(table.Name, usedNames));
            for (var r = 0; r < table.Rows.Count; r++)
            {
                var row = table.Rows[r];
                for (var c = 0; c < row.Count; c++)
                {
                    var cell = sheet.Cell(r + 1, c + 1);
                    switch (row[c])
                    {
                        case null:
                            break;
                        case double number:
                            cell.Value = number;
                            break;
                        case bool flag:
                            cell.Value = flag;
                            break;
                        case DateTime date:
                            cell.Value = date;
                            break;
                        case string text when inferTypes && r > 0 && TryInfer(text, out var typed):
                            cell.Value = typed;
                            break;
                        default:
                            cell.Value = row[c]!.ToString();
                            break;
                    }
                }
            }

            if (table.Rows.Count > 0)
            {
                sheet.Row(1).Style.Font.Bold = true;
                sheet.SheetView.FreezeRows(1);
            }

            sheet.Columns(1, Math.Max(1, Math.Min(table.ColumnCount, 200))).AdjustToContents(1, Math.Min(table.Rows.Count, 500));
        }

        workbook.SaveAs(path);
    }

    private static bool TryInfer(string text, out XLCellValue value)
    {
        value = text;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 20)
        {
            return false;
        }

        // Keep identifiers with leading zeros (ZIP codes, phone numbers) as text.
        if (trimmed.Length > 1 && trimmed[0] == '0' && char.IsDigit(trimmed[1]))
        {
            return false;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && trimmed.Count(char.IsDigit) <= 15)
        {
            value = number;
            return true;
        }

        if (DateTime.TryParseExact(trimmed, ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            value = date;
            return true;
        }

        return false;
    }

    private static void WriteDelimited(Table table, string path, char delimiter)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        CsvFormat.Write(writer, table.Rows.Select(r => (IReadOnlyList<string>)r.Select(FormatInvariant).ToList()), delimiter);
    }

    private static void WriteJson(Table table, string path, bool hasHeader)
    {
        var options = new JsonWriterOptions { Indented = true };
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, options);
        writer.WriteStartArray();
        if (hasHeader && table.Rows.Count > 0)
        {
            var headers = MakeUniqueHeaders(table.Rows[0]);
            foreach (var row in table.Rows.Skip(1))
            {
                writer.WriteStartObject();
                for (var c = 0; c < headers.Count; c++)
                {
                    writer.WritePropertyName(headers[c]);
                    WriteJsonValue(writer, c < row.Count ? row[c] : null, inferFromText: true);
                }

                writer.WriteEndObject();
            }
        }
        else
        {
            foreach (var row in table.Rows)
            {
                writer.WriteStartArray();
                foreach (var value in row)
                {
                    WriteJsonValue(writer, value, inferFromText: true);
                }

                writer.WriteEndArray();
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value, bool inferFromText)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case double number when double.IsFinite(number):
                writer.WriteNumberValue(number);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case DateTime date:
                writer.WriteStringValue(date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("s", CultureInfo.InvariantCulture));
                break;
            case string text when inferFromText && text.Length > 0 && !(text.Length > 1 && text[0] == '0' && char.IsDigit(text[1])) &&
                                  double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed):
                writer.WriteNumberValue(parsed);
                break;
            case string text when text.Length == 0:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteHtml(IReadOnlyList<Table> tables, string path, bool hasHeader, string title)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        builder.Append("<title>").Append(WebUtility.HtmlEncode(title)).AppendLine("</title>");
        builder.AppendLine("<style>body{font-family:Segoe UI,system-ui,sans-serif;margin:2rem}table{border-collapse:collapse;margin-bottom:2rem}th,td{border:1px solid #ccc;padding:.3rem .6rem;text-align:left}th{background:#f3f3f3}td.n{text-align:right}</style>");
        builder.AppendLine("</head><body>");
        foreach (var table in tables)
        {
            if (tables.Count > 1)
            {
                builder.Append("<h2>").Append(WebUtility.HtmlEncode(table.Name)).AppendLine("</h2>");
            }

            builder.AppendLine("<table>");
            for (var r = 0; r < table.Rows.Count; r++)
            {
                var header = hasHeader && r == 0;
                builder.Append("<tr>");
                foreach (var value in table.Rows[r])
                {
                    var tag = header ? "th" : "td";
                    var numeric = !header && value is double;
                    builder.Append('<').Append(tag).Append(numeric ? " class=\"n\">" : ">")
                        .Append(WebUtility.HtmlEncode(FormatInvariant(value)))
                        .Append("</").Append(tag).Append('>');
                }

                builder.AppendLine("</tr>");
            }

            builder.AppendLine("</table>");
        }

        builder.AppendLine("</body></html>");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static string FormatInvariant(object? value) => value switch
    {
        null => string.Empty,
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        bool flag => flag ? "TRUE" : "FALSE",
        DateTime date => date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static List<string> MakeUniqueHeaders(List<object?> headerRow)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        for (var i = 0; i < headerRow.Count; i++)
        {
            var name = FormatInvariant(headerRow[i]).Trim();
            if (name.Length == 0)
            {
                name = $"column{i + 1}";
            }

            var unique = name;
            for (var n = 2; !used.Add(unique); n++)
            {
                unique = $"{name}_{n}";
            }

            result.Add(unique);
        }

        return result;
    }

    private static string UniqueSheetName(string name, HashSet<string> used)
    {
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('\'');
        if (cleaned.Length == 0)
        {
            cleaned = "Sheet";
        }

        cleaned = cleaned.Length > 31 ? cleaned[..31] : cleaned;
        var unique = cleaned;
        for (var n = 2; !used.Add(unique); n++)
        {
            var suffix = $" ({n})";
            unique = (cleaned.Length + suffix.Length > 31 ? cleaned[..(31 - suffix.Length)] : cleaned) + suffix;
        }

        return unique;
    }

    private static Fidelity FidelityFor(string source, string target) => (source, target) switch
    {
        ("xlsx", _) => Fidelity.Extraction,
        (_, "xlsx") => Fidelity.Lossless,
        _ => Fidelity.Lossless,
    };

    private static string? NoteFor(string source, string target) => (source, target) switch
    {
        ("xlsx", "csv" or "tsv") => "Exports cell values; formatting, charts and formulas are not kept",
        ("xlsx", "json") => "Each row becomes a record keyed by the header row",
        ("xlsx", "html") => "Exports cell values as an HTML table",
        ("json", _) => "Expects an array of records; nested values are kept as JSON text",
        (_, "xlsx") => "Numbers and ISO dates are detected; values with leading zeros stay text",
        _ => null,
    };

    private static OptionDescriptor[] OptionsFor(string source, string target)
    {
        var options = new List<OptionDescriptor>();
        if (source == "xlsx")
        {
            options.Add(OptionDescriptor.Choice("sheets", "Sheets", "first", new("first", "First sheet"), new("all", "All sheets")));
        }

        if (target == "csv")
        {
            options.Add(OptionDescriptor.Choice("delimiter", "Separator", ",", new(",", "Comma ( , )"), new(";", "Semicolon ( ; )", "Used by Excel in many European locales")));
        }

        if (target is "json" or "html")
        {
            options.Add(OptionDescriptor.Toggle("header", "First row is a header", true));
        }

        if (target == "xlsx" && source != "json")
        {
            options.Add(OptionDescriptor.Toggle("inferTypes", "Detect numbers and dates", true) with { IsAdvanced = true });
        }

        return [.. options];
    }
}
