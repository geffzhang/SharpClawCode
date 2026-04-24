using System.Text;

namespace SharpClaw.Code.Tools.Utilities;

/// <summary>
/// Helpers shared by file edit tools for computing post-edit snippets with 1-based line numbers.
/// </summary>
internal static class EditSnippetBuilder
{
    /// <summary>
    /// Default number of context lines shown on each side of an edit in post-edit snippets.
    /// </summary>
    public const int DefaultContextLines = 3;

    /// <summary>
    /// Builds a compact, line-numbered preview of the lines surrounding the first differing line
    /// between <paramref name="updatedContent"/> and <paramref name="originalContent"/>.
    /// </summary>
    /// <remarks>
    /// The snippet is designed to give the caller enough context to validate an edit without a
    /// follow-up read. When no difference can be located the method returns
    /// <see langword="null"/> with zero start/end line numbers.
    /// </remarks>
    public static (string? Snippet, int StartLine, int EndLine) Build(
        string originalContent,
        string updatedContent,
        int contextLines = DefaultContextLines)
    {
        if (updatedContent.Length == 0)
        {
            return (string.Empty, 0, 0);
        }

        var originalLines = SplitLines(originalContent);
        var updatedLines = SplitLines(updatedContent);

        var firstDiff = FindFirstDifferentLine(originalLines, updatedLines);
        if (firstDiff < 0)
        {
            firstDiff = 0;
        }

        var start = Math.Max(0, firstDiff - contextLines);
        var end = Math.Min(updatedLines.Length - 1, firstDiff + contextLines);

        var builder = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(i + 1).Append('|').Append(updatedLines[i]);
        }

        return (builder.ToString(), start + 1, end + 1);
    }

    private static string[] SplitLines(string content)
        => content.Length == 0
            ? []
            : content.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray();

    private static int FindFirstDifferentLine(string[] original, string[] updated)
    {
        var limit = Math.Min(original.Length, updated.Length);
        for (var i = 0; i < limit; i++)
        {
            if (!string.Equals(original[i], updated[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return original.Length == updated.Length ? -1 : limit;
    }
}
