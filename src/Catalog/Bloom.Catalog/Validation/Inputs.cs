using System.Text.RegularExpressions;
using Bloom.Catalog.Errors;

namespace Bloom.Catalog.Validation;

/// <summary>Input checks of plan R14, R19 and R20; each throws <c>VALIDATION_FAILED</c> naming the proto field.</summary>
public static partial class Inputs
{
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z")] private static partial Regex SlugPattern();

    /// <summary>Length in Unicode scalar values, not UTF-16 units.</summary>
    private static int Scalars(string s) => s.EnumerateRunes().Count();

    /// <summary>Trimmed text of 1 to <paramref name="max"/> scalars.</summary>
    public static string Name(string raw, string field, int max)
    {
        var text = raw.Trim();
        if (Scalars(text) is 0 || Scalars(text) > max) throw CatalogErrors.Validation(field, $"{field} must be 1 to {max} characters.");
        return text;
    }

    public static string Slug(string raw) =>
        raw.Length <= 100 && SlugPattern().IsMatch(raw)
            ? raw
            : throw CatalogErrors.Validation("slug", "slug must be lowercase letters and digits separated by single hyphens, at most 100 characters.");

    /// <summary>Trimmed text of at most <paramref name="max"/> scalars; null or blank gives null.</summary>
    public static string? OptionalText(string? raw, string field, int max)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return Scalars(text) <= max ? text : throw CatalogErrors.Validation(field, $"{field} must be at most {max} characters.");
    }

    /// <summary><paramref name="page"/> 0 is page 1; <paramref name="size"/> 0 is <paramref name="default"/>.</summary>
    public static (int Page, int Size) Paging(int page, int size, int @default, int max)
    {
        if (page < 0) throw CatalogErrors.Validation("page", "page must be 1 or more.");
        if (size < 0 || size > max) throw CatalogErrors.Validation("page_size", $"page_size must be 1 to {max}.");
        return (page == 0 ? 1 : page, size == 0 ? @default : size);
    }
}
