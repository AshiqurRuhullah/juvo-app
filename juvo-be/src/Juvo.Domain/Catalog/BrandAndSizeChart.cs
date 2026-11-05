using Juvo.Domain.Common;

namespace Juvo.Domain.Catalog;

public sealed class Brand : SoftDeletableEntity
{
    private Brand()
    {
    }

    public Brand(string nameEn, string? nameBn, string slug)
    {
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        Slug = slug;
    }

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public string Slug { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    public void Update(string nameEn, string? nameBn, string slug, bool isActive)
    {
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        Slug = slug;
        IsActive = isActive;
    }
}

/// <summary>A size guide shown on product pages. The table is stored as JSON: <c>{ "columns": [...], "rows": [[...]] }</c>.</summary>
public sealed class SizeChart : SoftDeletableEntity
{
    private SizeChart()
    {
    }

    public SizeChart(string nameEn, string? nameBn, string tableJson, Guid? categoryId)
    {
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        TableJson = tableJson;
        CategoryId = categoryId;
    }

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public string TableJson { get; private set; } = "{}";

    /// <summary>Suggested for products of this category; a product may still pick any chart.</summary>
    public Guid? CategoryId { get; private set; }

    public void Update(string nameEn, string? nameBn, string tableJson, Guid? categoryId)
    {
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        TableJson = tableJson;
        CategoryId = categoryId;
    }
}
