namespace Ledger.Core.Models;

/// <summary>A hierarchical spending/income classification.</summary>
public class Category
{
    public int Id { get; set; }
    public required string Name { get; set; }

    public int? ParentCategoryId { get; set; }
    public Category? ParentCategory { get; set; }
    public List<Category> Children { get; set; } = [];

    public CategoryType CategoryType { get; set; }

    /// <summary>Segoe Fluent Icons glyph (e.g. ""). Optional.</summary>
    public string? Icon { get; set; }

    public bool IsArchived { get; set; }
    public int SortOrder { get; set; }
}
