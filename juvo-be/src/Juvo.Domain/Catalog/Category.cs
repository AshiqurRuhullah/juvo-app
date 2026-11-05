using Juvo.Domain.Common;

namespace Juvo.Domain.Catalog;

/// <summary>A node of the category tree (for example Clothing, then T-shirts). Shared by every branch.</summary>
public sealed class Category : SoftDeletableEntity
{
    private Category()
    {
    }

    public Category(string nameEn, string? nameBn, string slug, Guid? parentId)
    {
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        Slug = slug;
        ParentId = parentId;
    }

    public Guid? ParentId { get; private set; }

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public string Slug { get; private set; } = string.Empty;

    /// <summary>Logical storage path of the category image, if any.</summary>
    public string? ImagePath { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; } = true;

    public void Update(string nameEn, string? nameBn, string slug, Guid? parentId, int sortOrder, bool isActive)
    {
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        Slug = slug;
        ParentId = parentId;
        SortOrder = sortOrder;
        IsActive = isActive;
    }

    public void SetImage(string? path) => ImagePath = path;

    public void SetSortOrder(int sortOrder) => SortOrder = sortOrder;
}
