namespace ContentStudio.Api.Services;

public record DiffLine(string Type, string? Text, int? LineA, int? LineB);
public record DiffStats(int Added, int Removed, int Unchanged, double SimilarityPct);

/// <summary>
/// Line-level diff between two prompt versions.
///
/// A hand-rolled LCS rather than a diff library: the inputs are prompts, so a
/// few hundred lines at most, and this keeps a visual-diff feature from adding
/// a dependency. Falls back gracefully on very large inputs, where an O(n·m)
/// table would be the wrong trade.
/// </summary>
public static class TextDiff
{
    private const int MaxLinesForLcs = 2000;

    public static List<DiffLine> Compute(string? a, string? b)
    {
        var linesA = Split(a);
        var linesB = Split(b);

        if (linesA.Length > MaxLinesForLcs || linesB.Length > MaxLinesForLcs)
            return CoarseDiff(linesA, linesB);

        // Standard LCS table, then walk it backwards to emit the edit script.
        var lcs = new int[linesA.Length + 1, linesB.Length + 1];
        for (var i = linesA.Length - 1; i >= 0; i--)
            for (var j = linesB.Length - 1; j >= 0; j--)
                lcs[i, j] = linesA[i] == linesB[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < linesA.Length && y < linesB.Length)
        {
            if (linesA[x] == linesB[y])
            {
                result.Add(new DiffLine("unchanged", linesA[x], x + 1, y + 1));
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new DiffLine("removed", linesA[x], x + 1, null));
                x++;
            }
            else
            {
                result.Add(new DiffLine("added", linesB[y], null, y + 1));
                y++;
            }
        }
        while (x < linesA.Length) result.Add(new DiffLine("removed", linesA[x], ++x, null));
        while (y < linesB.Length) result.Add(new DiffLine("added", linesB[y], null, ++y));

        return result;
    }

    /// <summary>Whole-block replace, for inputs too large to diff line by line.</summary>
    private static List<DiffLine> CoarseDiff(string[] a, string[] b)
    {
        var result = new List<DiffLine>();
        for (var i = 0; i < a.Length; i++) result.Add(new DiffLine("removed", a[i], i + 1, null));
        for (var j = 0; j < b.Length; j++) result.Add(new DiffLine("added", b[j], null, j + 1));
        return result;
    }

    public static DiffStats Stats(string? a, string? b)
    {
        var diff = Compute(a, b);
        var added = diff.Count(d => d.Type == "added");
        var removed = diff.Count(d => d.Type == "removed");
        var unchanged = diff.Count(d => d.Type == "unchanged");
        var total = added + removed + unchanged;
        var similarity = total == 0 ? 100.0 : Math.Round(100.0 * unchanged / total, 1);
        return new DiffStats(added, removed, unchanged, similarity);
    }

    private static string[] Split(string? s) =>
        string.IsNullOrEmpty(s) ? [] : s.Replace("\r\n", "\n").Split('\n');
}
