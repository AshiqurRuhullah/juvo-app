using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Juvo.Application.Abstractions;

namespace Juvo.Infrastructure.Storage;

/// <summary>
/// CSV (RFC 4180, UTF-8) and Excel (.xlsx, Microsoft's MIT-licensed Open XML SDK). The CSV code is our own so no third-party licence is
/// involved. Exported CSV text that could be read as a formula by a spreadsheet gets a leading apostrophe (CSV injection).
/// </summary>
internal sealed class TabularFiles : ITabularFile
{
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const int MaxColumns = 100;

    public TabularFormat Detect(ReadOnlySpan<byte> head) =>
        head.Length >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K' ? TabularFormat.Xlsx : TabularFormat.Csv;

    public string ContentType(TabularFormat format) => format == TabularFormat.Xlsx ? XlsxType : "text/csv; charset=utf-8";

    public string Extension(TabularFormat format) => format == TabularFormat.Xlsx ? "xlsx" : "csv";

    public TableData Read(Stream content, TabularFormat format, int maxRows) =>
        format == TabularFormat.Xlsx ? ReadXlsx(content, maxRows) : ReadCsv(content, maxRows);

    public byte[] Write(TableData table, TabularFormat format) => format == TabularFormat.Xlsx ? WriteXlsx(table) : WriteCsv(table);

    // ---- CSV --------------------------------------------------------------------------------------------

    private static TableData ReadCsv(Stream content, int maxRows)
    {
        string text;
        try
        {
            using var reader = new StreamReader(content, new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            throw new TabularFileException("The file is not UTF-8 text. Save the CSV as \"CSV UTF-8\" and try again.");
        }

        var delimiter = Sniff(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var any = false; // the current row has content (even an empty quoted cell)

        void EndCell()
        {
            row.Add(cell.ToString());
            cell.Clear();
        }

        void EndRow()
        {
            EndCell();
            if (any || row.Any(c => c.Length > 0))
            {
                if (row.Count > MaxColumns)
                {
                    throw new TabularFileException($"The file has more than {MaxColumns} columns.");
                }

                rows.Add(row);
                if (rows.Count > maxRows + 1)
                {
                    throw new TabularFileException($"The file has more than {maxRows} data rows. Split it into smaller files.");
                }
            }

            row = [];
            any = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"' when cell.Length == 0:
                    inQuotes = true;
                    any = true;
                    break;
                case var d when d == delimiter:
                    EndCell();
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    cell.Append(ch);
                    break;
            }
        }

        if (inQuotes)
        {
            throw new TabularFileException("The file has a quote that is never closed.");
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            EndRow();
        }

        return ToTable(rows);
    }

    private static char Sniff(string text)
    {
        var end = text.IndexOf('\n', StringComparison.Ordinal);
        var header = end < 0 ? text : text[..end];
        char[] candidates = [',', ';', '\t'];
        return candidates.OrderByDescending(c => header.Count(h => h == c)).First();
    }

    private static byte[] WriteCsv(TableData table)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(',', table.Headers.Select(h => Quote(h))));
        foreach (var row in table.Rows)
        {
            builder.Append("\r\n");
            for (var c = 0; c < table.Headers.Count; c++)
            {
                if (c > 0)
                {
                    builder.Append(',');
                }

                var value = c < row.Count ? row[c] : null;
                var numeric = table.NumericColumns?.Contains(table.Headers[c]) == true;
                builder.Append(Quote(value is not null && !numeric ? Neutralise(value) : value));
            }
        }

        builder.Append("\r\n");
        // A byte order mark makes Excel read the file as UTF-8, so Bengali text survives opening it by double click.
        return [.. Encoding.UTF8.GetPreamble(), .. new UTF8Encoding(false).GetBytes(builder.ToString())];
    }

    private static string Neutralise(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal) ||
               value.Contains('\r', StringComparison.Ordinal) || value[0] == ' ' || value[^1] == ' '
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    // ---- XLSX -------------------------------------------------------------------------------------------

    private static TableData ReadXlsx(Stream content, int maxRows)
    {
        try
        {
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            copy.Position = 0;
            using var document = SpreadsheetDocument.Open(copy, false);
            var workbook = document.WorkbookPart ?? throw new TabularFileException("The Excel file has no workbook.");
            var sheet = workbook.Workbook!.Descendants<Sheet>().FirstOrDefault() ?? throw new TabularFileException("The Excel file has no sheet.");
            var worksheet = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
            var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToList() ?? [];

            var rows = new List<List<string>>();
            foreach (var xmlRow in worksheet.Worksheet!.Descendants<Row>())
            {
                var cells = new List<string>();
                foreach (var cell in xmlRow.Elements<Cell>())
                {
                    var index = ColumnIndex(cell.CellReference?.Value, cells.Count);
                    if (index >= MaxColumns)
                    {
                        throw new TabularFileException($"The file has more than {MaxColumns} columns.");
                    }

                    while (cells.Count < index)
                    {
                        cells.Add(string.Empty);
                    }

                    cells.Add(CellText(cell, shared));
                }

                if (cells.Any(c => c.Length > 0))
                {
                    rows.Add(cells);
                    if (rows.Count > maxRows + 1)
                    {
                        throw new TabularFileException($"The file has more than {maxRows} data rows. Split it into smaller files.");
                    }
                }
            }

            return ToTable(rows);
        }
        catch (TabularFileException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OpenXmlPackageException or InvalidOperationException or ArgumentException or FormatException or System.Xml.XmlException)
        {
            throw new TabularFileException("The file is not a valid Excel (.xlsx) file.");
        }
    }

    private static string CellText(Cell cell, List<string> shared)
    {
        var raw = cell.CellValue?.Text;
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        if (raw is null)
        {
            return string.Empty;
        }

        if (cell.DataType?.Value == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Count ? shared[i] : string.Empty;
        }

        if (cell.DataType?.Value == CellValues.Boolean)
        {
            return raw == "1" ? "TRUE" : "FALSE";
        }

        return raw;
    }

    private static int ColumnIndex(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return fallback;
        }

        var index = 0;
        foreach (var ch in reference)
        {
            if (ch is < 'A' or > 'Z')
            {
                break;
            }

            index = (index * 26) + (ch - 'A' + 1);
        }

        return index == 0 ? fallback : index - 1;
    }

    private static byte[] WriteXlsx(TableData table)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();

            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new Stylesheet(
                new Fonts(new Font(), new Font(new Bold())) { Count = 2 },
                new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
                new Borders(new Border()) { Count = 1 },
                new CellFormats(new CellFormat(), new CellFormat { FontId = 1, ApplyFont = true }) { Count = 2 });
            styles.Stylesheet.Save();

            var sheetData = new SheetData();
            sheetData.AppendChild(DataRow(table.Headers.Select(h => ((string?)h, false)).ToList(), header: true));
            foreach (var row in table.Rows)
            {
                var cells = new List<(string?, bool)>();
                for (var c = 0; c < table.Headers.Count; c++)
                {
                    cells.Add((c < row.Count ? row[c] : null, table.NumericColumns?.Contains(table.Headers[c]) == true));
                }

                sheetData.AppendChild(DataRow(cells, header: false));
            }

            var columns = new Columns();
            for (var c = 0; c < table.Headers.Count; c++)
            {
                columns.AppendChild(new Column { Min = (uint)c + 1, Max = (uint)c + 1, Width = Math.Clamp(table.Headers[c].Length + 4, 12, 40), CustomWidth = true });
            }

            var worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new Worksheet(
                new SheetViews(new SheetView(new Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen }) { WorkbookViewId = 0 }),
                columns,
                sheetData);
            worksheet.Worksheet.Save();

            workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = "Products" }));
            workbook.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static Row DataRow(IReadOnlyList<(string? Value, bool Numeric)> cells, bool header)
    {
        var row = new Row();
        foreach (var (value, numeric) in cells)
        {
            if (string.IsNullOrEmpty(value))
            {
                row.AppendChild(new Cell { DataType = CellValues.InlineString, StyleIndex = header ? 1u : 0u, InlineString = new InlineString(new Text(string.Empty)) });
                continue;
            }

            if (numeric && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            {
                row.AppendChild(new Cell { DataType = CellValues.Number, CellValue = new CellValue(value) });
            }
            else
            {
                row.AppendChild(new Cell
                {
                    DataType = CellValues.InlineString,
                    StyleIndex = header ? 1u : 0u,
                    InlineString = new InlineString(new Text(StripInvalidXml(value)) { Space = SpaceProcessingModeValues.Preserve }),
                });
            }
        }

        return row;
    }

    private static string StripInvalidXml(string value) =>
        new([.. value.Where(ch => ch is '\t' or '\n' or '\r' || (ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF && !char.IsSurrogate(ch)) || (char.IsSurrogate(ch)))]);

    private static TableData ToTable(List<List<string>> rows)
    {
        if (rows.Count == 0)
        {
            throw new TabularFileException("The file is empty. The first row must be the column names.");
        }

        var headers = rows[0].Select(h => h.Trim()).ToList();
        while (headers.Count > 0 && headers[^1].Length == 0)
        {
            headers.RemoveAt(headers.Count - 1);
        }

        var data = rows.Skip(1).Select(r => (IReadOnlyList<string?>)[.. r.Take(headers.Count).Select(c => (string?)c).Concat(Enumerable.Repeat<string?>(string.Empty, Math.Max(0, headers.Count - r.Count)))]).ToList();
        return new TableData(headers, data);
    }
}
