using System.Text.RegularExpressions;

namespace ShipTime4x4.Hotfolder.Services;

public static partial class DestinationAddressParser
{
    public static string Parse(string? recognizedText, string? tsvText, string recipient)
    {
        var positioned = ParsePositioned(tsvText, recipient);
        return positioned.Length > 0 ? positioned : ParseLines(recognizedText, recipient);
    }

    private static string ParsePositioned(string? tsvText, string recipient)
    {
        var words = ReadWords(tsvText);
        var marker = words.Where(word => IsToMarker(word.Text))
            .OrderBy(word => word.Top).ThenByDescending(word => word.Left).FirstOrDefault();
        if (marker is null)
            return string.Empty;

        var clusters = words.GroupBy(word => (word.Block, word.Paragraph, word.Line))
            .SelectMany(group => SplitClusters(group.OrderBy(word => word.Left).ToArray()))
            .Where(cluster => cluster.Top > marker.Top + Math.Max(3, marker.Height / 2))
            .Where(cluster => cluster.Top < marker.Top + 1050)
            .Where(cluster => cluster.Left >= marker.Left - 180 && cluster.Left < marker.Left + 1350)
            .OrderBy(cluster => cluster.Top).ThenBy(cluster => cluster.Left)
            .ToArray();

        var address = new List<string>();
        foreach (var cluster in clusters)
        {
            var text = Normalize(cluster.Text);
            if (SameRecipient(text, recipient) || IsToMarker(text))
                continue;
            if (IsStop(text))
            {
                if (address.Count > 0)
                    break;
                continue;
            }
            if (!LooksLikeAddress(text, address.Count > 0))
                continue;
            address.Add(text);
            if (PostalCode().IsMatch(text) || address.Count == 4)
                break;
        }
        return string.Join(", ", address);
    }

    private static string ParseLines(string? text, string recipient)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var lines = text.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(line => line.Length > 0).ToArray();
        var marker = Array.FindIndex(lines, line => IsToMarker(line));
        if (marker < 0)
            return string.Empty;
        var address = new List<string>();
        for (var index = marker + 1; index < lines.Length && index <= marker + 9; index++)
        {
            var line = lines[index];
            if (SameRecipient(line, recipient))
                continue;
            if (IsStop(line))
            {
                if (address.Count > 0) break;
                continue;
            }
            if (!LooksLikeAddress(line, address.Count > 0))
                continue;
            address.Add(line);
            if (PostalCode().IsMatch(line) || address.Count == 4) break;
        }
        return string.Join(", ", address);
    }

    private static IReadOnlyList<Word> ReadWords(string? tsvText)
    {
        if (string.IsNullOrWhiteSpace(tsvText)) return [];
        var words = new List<Word>();
        foreach (var row in tsvText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var fields = row.TrimEnd('\r').Split('\t');
            if (fields.Length < 12 || fields[0] != "5" || string.IsNullOrWhiteSpace(fields[11])) continue;
            if (int.TryParse(fields[2], out var block) && int.TryParse(fields[3], out var paragraph) &&
                int.TryParse(fields[4], out var line) && int.TryParse(fields[6], out var left) &&
                int.TryParse(fields[7], out var top) && int.TryParse(fields[8], out var width) &&
                int.TryParse(fields[9], out var height))
                words.Add(new Word(block, paragraph, line, left, top, width, height, Normalize(fields[11])));
        }
        return words;
    }

    private static IEnumerable<Cluster> SplitClusters(IReadOnlyList<Word> ordered)
    {
        var current = new List<Word>();
        foreach (var word in ordered)
        {
            if (current.Count > 0)
            {
                var previous = current[^1];
                if (word.Left - (previous.Left + previous.Width) > Math.Max(70, Math.Max(previous.Height, word.Height) * 4))
                {
                    yield return ToCluster(current);
                    current.Clear();
                }
            }
            current.Add(word);
        }
        if (current.Count > 0) yield return ToCluster(current);
    }

    private static Cluster ToCluster(IReadOnlyList<Word> words) => new(words.Min(x => x.Left), words.Min(x => x.Top),
        words.Max(x => x.Left + x.Width) - words.Min(x => x.Left),
        words.Max(x => x.Top + x.Height) - words.Min(x => x.Top), string.Join(" ", words.Select(x => x.Text)));

    private static bool LooksLikeAddress(string text, bool alreadyStarted) =>
        PostalCode().IsMatch(text) || Street().IsMatch(text) ||
        (alreadyStarted && text.Any(char.IsLetter) && text.Length is >= 3 and <= 80);

    private static bool IsStop(string text) => Phone().IsMatch(text) ||
        new[] { "DATE", "WEIGHT", "PIECES", "PUROLATOR", "GROUND", "SERVICE", "PIN", "FROM", "REF" }
            .Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool SameRecipient(string text, string recipient) =>
        NormalizeKey(text) == NormalizeKey(recipient);

    private static bool IsToMarker(string value)
    {
        var compact = Regex.Replace(value, "[^A-Z]", string.Empty, RegexOptions.IgnoreCase);
        return compact is "TO" or "TOA" or "SHIPTO" or "DELIVERTO" or "CONSIGNEE";
    }

    private static string Normalize(string value) => Whitespace().Replace(value.Trim(), " ");
    private static string NormalizeKey(string value) => Regex.Replace(value, "[^A-Z0-9]", string.Empty, RegexOptions.IgnoreCase);

    [GeneratedRegex(@"\b[A-Z]\d[A-Z][ -]?\d[A-Z]\d\b", RegexOptions.IgnoreCase)] private static partial Regex PostalCode();
    [GeneratedRegex(@"\b\d{1,6}\s+.+(?:ST|STREET|RD|ROAD|AVE|AVENUE|DR|DRIVE|BLVD|LANE|LN|HWY|WAY|CT|COURT)\b", RegexOptions.IgnoreCase)] private static partial Regex Street();
    [GeneratedRegex(@"\b(?:\+?1[ -]?)?\d{3}[ -.\)]*\d{3}[ -.]*\d{4}\b")] private static partial Regex Phone();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    private sealed record Word(int Block, int Paragraph, int Line, int Left, int Top, int Width, int Height, string Text);
    private sealed record Cluster(int Left, int Top, int Width, int Height, string Text);
}
