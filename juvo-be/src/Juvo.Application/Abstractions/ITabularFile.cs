namespace Juvo.Application.Abstractions;

public enum TabularFormat
{
    Csv,
    Xlsx,
}

/// <summary>A sheet: a header row and the data rows under it. Cells are text; <see cref="NumericColumns"/> names columns written as numbers in Excel.</summary>
public sealed record TableData(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string?>> Rows, IReadOnlySet<string>? NumericColumns = null);

/// <summary>The file is not a readable CSV or Excel file, or it is too large. The message is safe to show to staff.</summary>
public sealed class TabularFileException(string message) : Exception(message);

/// <summary>Reads and writes CSV and Excel (.xlsx) files for bulk import and export. Implemented in Infrastructure.</summary>
public interface ITabularFile
{
    /// <summary>Excel files are zip files (start with <c>PK</c>); anything else is treated as CSV.</summary>
    TabularFormat Detect(ReadOnlySpan<byte> head);

    /// <summary>Reads the first sheet (or the CSV). Rows that are completely empty are skipped. Throws <see cref="TabularFileException"/>.</summary>
    TableData Read(Stream content, TabularFormat format, int maxRows);

    byte[] Write(TableData table, TabularFormat format);

    string ContentType(TabularFormat format);

    string Extension(TabularFormat format);
}
