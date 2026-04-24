namespace SharpClaw.Code.Tools.Utilities;

/// <summary>
/// Helpers for performing ordinal string replacements with explicit occurrence counting,
/// shared by <c>edit_file</c> and <c>multi_edit_file</c> tools.
/// </summary>
internal static class StringReplacer
{
    /// <summary>
    /// Counts the number of non-overlapping ordinal occurrences of <paramref name="value"/> in <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// Treats an empty <paramref name="value"/> as zero occurrences to avoid infinite-loop semantics.
    /// </remarks>
    public static int CountOccurrences(string source, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>
    /// Replaces the first ordinal occurrence of <paramref name="oldValue"/> in <paramref name="source"/> with <paramref name="newValue"/>.
    /// </summary>
    /// <returns>The updated string; returns the original reference when no occurrence is found.</returns>
    public static string ReplaceFirst(string source, string oldValue, string newValue)
    {
        var index = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0)
        {
            return source;
        }

        return string.Concat(
            source.AsSpan(0, index),
            newValue,
            source.AsSpan(index + oldValue.Length));
    }

    /// <summary>
    /// Replaces every non-overlapping ordinal occurrence of <paramref name="oldValue"/> in <paramref name="source"/> with <paramref name="newValue"/>.
    /// </summary>
    public static string ReplaceAll(string source, string oldValue, string newValue)
        => string.IsNullOrEmpty(oldValue)
            ? source
            : source.Replace(oldValue, newValue, StringComparison.Ordinal);
}
