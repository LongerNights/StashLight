using System.Text;
using System.Text.RegularExpressions;

namespace InventorySearch.Core;

public static partial class Text
{
    [GeneratedRegex("<[^>]*>")]
    private static partial Regex RichTextTag();

    /// <summary>Removes rich-text tags like &lt;color=red&gt; and lowercases (invariant).</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains('<')) s = RichTextTag().Replace(s, "");
        return s.ToLowerInvariant();
    }

    public static string StripRichText(string? s)
        => string.IsNullOrEmpty(s) ? "" : (s.Contains('<') ? RichTextTag().Replace(s, "") : s);

    /// <summary>Starts of words in an already-normalized string (after a non-letter/digit boundary).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static bool HasWordPrefix(string normalized, string term)
    {
        int i = 0;
        while (true)
        {
            i = normalized.IndexOf(term, i, StringComparison.Ordinal);
            if (i < 0) return false;
            if (i == 0 || !char.IsLetterOrDigit(normalized[i - 1])) return true;
            i++;
        }
    }

    /// <summary>Sorted, de-duplicated 3-gram hashes (with padding) for fuzzy matching.</summary>
    public static int[] Trigrams(string normalized)
    {
        if (normalized.Length == 0) return Array.Empty<int>();
        var padded = new StringBuilder(normalized.Length + 4).Append("  ").Append(normalized).Append(' ').ToString();
        var set = new HashSet<int>();
        for (int i = 0; i + 3 <= padded.Length; i++)
            set.Add((padded[i] * 31 + padded[i + 1]) * 31 + padded[i + 2]);
        var arr = set.ToArray();
        Array.Sort(arr);
        return arr;
    }

    /// <summary>|a ∩ b| / |query|, both sorted.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static double Containment(int[] query, int[] target)
    {
        if (query.Length == 0) return 0;
        int i = 0, j = 0, hit = 0;
        while (i < query.Length && j < target.Length)
        {
            if (query[i] == target[j]) { hit++; i++; j++; }
            else if (query[i] < target[j]) i++;
            else j++;
        }
        return (double)hit / query.Length;
    }
}
