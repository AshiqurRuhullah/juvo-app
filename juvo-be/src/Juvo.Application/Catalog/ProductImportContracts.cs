using Juvo.Domain.Catalog;

namespace Juvo.Application.Catalog;

/// <summary><see cref="Create"/> refuses products that already exist; <see cref="Upsert"/> updates them (a blank cell keeps the current value).</summary>
public enum ImportMode
{
    Create,
    Upsert,
}

/// <summary>One line of the validation report. Row 0 means the file as a whole; Row 1 is the header row.</summary>
public sealed record ImportIssueDto(int Row, string? Column, string Severity, string Message);

/// <summary>
/// What the import did (or, for a dry run or a file with errors, would have done). Nothing is saved unless <see cref="Committed"/> is true:
/// a file with any error is rejected as a whole.
/// </summary>
public sealed record ImportReportDto(
    bool DryRun, bool Committed, ImportMode Mode, string Format, int RowsRead, int ProductsCreated, int ProductsUpdated, int VariantsCreated,
    int VariantsUpdated, int StockLinesApplied, int ErrorCount, int WarningCount, bool IssuesTruncated, IReadOnlyList<ImportIssueDto> Issues);

public sealed record ExportQuery(ProductStatus? Status, Guid? CategoryId, Guid? BranchId);

public sealed record ExportFileDto(byte[] Content, string ContentType, string FileName);
