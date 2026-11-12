using System.Globalization;

namespace Bloom.Catalog.Domain;

/// <summary>What a review shows as its author (08-catalog §4.1 <c>reviewer_names</c>, spec §4 "First L.", plan R22).</summary>
public static class ReviewerNames
{
    /// <summary>Used when no <c>reviewer_names</c> row exists yet (08-catalog §8).</summary>
    public const string Fallback = "Bloom customer";

    /// <summary>REV-R08: what a review of a deleted account shows.</summary>
    public const string FormerCustomer = "Former customer";

    /// <summary>
    /// Splits on Unicode whitespace: two or more words give the first word and the first text element of the last word with a dot
    /// (<c>Mary Jane Watson</c> → <c>Mary W.</c>), one word is kept, none gives <see cref="Fallback"/>. No case change.
    /// </summary>
    public static string Derive(string? name)
    {
        var words = name?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        return words.Length switch
        {
            0 => Fallback,
            1 => words[0],
            _ => $"{words[0]} {StringInfo.GetNextTextElement(words[^1])}.",
        };
    }
}
