using System.Globalization;
using System.Text;

namespace InfiniContext.Core.Tasks;

/// <summary>
/// Pure helper that turns a free-form task title into the kebab-case slug used in
/// <c>.task/&lt;id&gt;-&lt;slug&gt;/</c> folder names. ASCII-safe: diacritics are stripped via
/// Unicode NFD decomposition, non-ASCII letters/digits are dropped (joined with a hyphen),
/// and runs of non-alphanumeric characters collapse into a single hyphen.
/// </summary>
public static class Slugify
{
    /// <summary>Default cap on slug length. Folders with longer slugs hit Windows path limits.</summary>
    public const int DefaultMaxLength = 60;

    /// <summary>Fallback when the input contains no usable characters.</summary>
    public const string Fallback = "task";

    public static string For(string title, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(title)) return Fallback;
        if (maxLength < 4) throw new ArgumentOutOfRangeException(nameof(maxLength), "maxLength must be at least 4 to fit the fallback slug.");

        var sb = new StringBuilder(capacity: Math.Min(title.Length, maxLength));
        foreach (var ch in title.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;

            if (ch <= 0x7F && char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                // Anything else (whitespace, punctuation, non-ASCII letters) becomes a hyphen,
                // collapsing runs into a single separator.
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) return Fallback;

        if (slug.Length > maxLength)
        {
            slug = slug[..maxLength];
            // Don't cut mid-word if a hyphen sits in the back half.
            var lastHyphen = slug.LastIndexOf('-');
            if (lastHyphen > maxLength / 2) slug = slug[..lastHyphen];
            slug = slug.TrimEnd('-');
            if (slug.Length == 0) return Fallback;
        }

        return slug;
    }
}
