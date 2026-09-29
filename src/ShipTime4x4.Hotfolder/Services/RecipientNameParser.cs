using System.Text.RegularExpressions;

namespace ShipTime4x4.Hotfolder.Services;

public static partial class RecipientNameParser
{
    private static readonly string[] StopPhrases =
    [
        "SHIP FROM", "FROM:", "SERVICE", "TRACKING", "REFERENCE", "DATE", "WEIGHT", "PACKAGE", "PUROLATOR",
        "EXPRESS", "GROUND", "POSTAL", "ZIP", "PHONE", "TEL", "ROUTE", "SORT", "PIN"
    ];

    public static string Parse(string? recognizedText)
        => Parse(recognizedText, null);

    public static string Parse(string? recognizedText, string? tsvText)
    {
        var positioned = ParsePositioned(tsvText);
        if (positioned != "Unknown recipient")
            return positioned;
        if (string.IsNullOrWhiteSpace(recognizedText))
            return "Unknown recipient";

        var lines = recognizedText.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(x => x.Length > 0)
            .ToArray();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!ShipToMarker().IsMatch(line))
                continue;

            var inline = ShipToMarker().Replace(line, string.Empty).Trim(' ', ':', '-');
            if (IsCandidate(inline))
                return ToDisplayName(inline);

            for (var offset = 1; offset <= 4 && index + offset < lines.Length; offset++)
            {
                var candidate = lines[index + offset];
                if (IsCandidate(candidate))
                    return ToDisplayName(candidate);
            }
        }

        return "Unknown recipient";
    }

    private static string ParsePositioned(string? tsvText)
    {
        if (string.IsNullOrWhiteSpace(tsvText))
            return "Unknown recipient";
        var words = new List<PositionedWord>();
        foreach (var row in tsvText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var fields = row.TrimEnd('\r').Split('\t');
            if (fields.Length < 12 || fields[0] != "5" || string.IsNullOrWhiteSpace(fields[11]))
                continue;
            if (!int.TryParse(fields[2], out var block) || !int.TryParse(fields[3], out var paragraph) ||
                !int.TryParse(fields[4], out var line) || !int.TryParse(fields[6], out var left) ||
                !int.TryParse(fields[7], out var top) || !int.TryParse(fields[8], out var width) ||
                !int.TryParse(fields[9], out var height))
                continue;
            words.Add(new PositionedWord(block, paragraph, line, left, top, width, height, Normalize(fields[11])));
        }

        var marker = words.Where(word => IsToMarker(word.Text))
            .OrderBy(word => word.Top)
            .ThenByDescending(word => word.Left)
            .FirstOrDefault();
        if (marker is null)
            return "Unknown recipient";

        var clusters = new List<PositionedCluster>();
        foreach (var group in words.GroupBy(word => (word.Block, word.Paragraph, word.Line)))
        {
            var ordered = group.OrderBy(word => word.Left).ToArray();
            var current = new List<PositionedWord>();
            foreach (var word in ordered)
            {
                if (current.Count > 0)
                {
                    var previous = current[^1];
                    var gap = word.Left - (previous.Left + previous.Width);
                    var splitGap = Math.Max(70, Math.Max(previous.Height, word.Height) * 4);
                    if (gap > splitGap)
                    {
                        clusters.Add(ToCluster(current));
                        current.Clear();
                    }
                }
                current.Add(word);
            }
            if (current.Count > 0)
                clusters.Add(ToCluster(current));
        }

        var candidate = clusters
            .Where(cluster => cluster.Top >= marker.Top + Math.Max(4, marker.Height / 2))
            .Where(cluster => cluster.Top <= marker.Top + 1100)
            .Where(cluster => cluster.Left >= marker.Left - 180)
            .Where(cluster => cluster.Left <= marker.Left + 1300)
            .Where(cluster => IsCandidate(cluster.Text))
            .OrderBy(cluster => (cluster.Top - marker.Top) + Math.Abs(cluster.Left - marker.Left) * 0.35)
            .FirstOrDefault();
        return candidate is null ? "Unknown recipient" : ToDisplayName(candidate.Text);
    }

    private static PositionedCluster ToCluster(IReadOnlyList<PositionedWord> words) => new(
        words.Min(x => x.Left), words.Min(x => x.Top), words.Max(x => x.Left + x.Width) - words.Min(x => x.Left),
        words.Max(x => x.Top + x.Height) - words.Min(x => x.Top), string.Join(" ", words.Select(x => x.Text)));

    private static bool IsToMarker(string value)
    {
        var compact = Regex.Replace(value, "[^A-Z]", string.Empty, RegexOptions.IgnoreCase);
        return compact is "TO" or "TOA" or "SHIPTO" or "DELIVERTO" or "CONSIGNEE";
    }

    private static bool IsCandidate(string value)
    {
        if (value.Length is < 2 or > 80 || !value.Any(char.IsLetter))
            return false;
        if (StopPhrases.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (value.Count(char.IsDigit) > value.Length / 3)
            return false;
        return true;
    }

    private static string Normalize(string value) => Whitespace().Replace(value.Trim(), " ");

    private static string ToDisplayName(string value)
    {
        value = Regex.Replace(value, @"^[^A-Za-z0-9]+|[^A-Za-z0-9.&'() -]+$", string.Empty).Trim();
        return value.Length == 0 ? "Unknown recipient" : value;
    }

    [GeneratedRegex(@"\b(?:SHIP\s*TO|DELIVER\s*TO|CONSIGNEE)\b\s*:?")]
    private static partial Regex ShipToMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed record PositionedWord(int Block, int Paragraph, int Line, int Left, int Top, int Width, int Height, string Text);
    private sealed record PositionedCluster(int Left, int Top, int Width, int Height, string Text);
}
