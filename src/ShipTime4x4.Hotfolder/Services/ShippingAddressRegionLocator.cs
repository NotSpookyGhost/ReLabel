using System.Text.RegularExpressions;
using ShipTime4x4.Core.Raster;

namespace ShipTime4x4.Hotfolder.Services;

public static partial class ShippingAddressRegionLocator
{
    public static IReadOnlyList<PixelRect> Locate(string? tsvText, int pageWidth, int pageHeight, int dpi)
    {
        var words = ReadWords(tsvText);
        if (words.Count == 0) return [];

        var clusters = words.GroupBy(word => (word.Block, word.Paragraph, word.Line))
            .SelectMany(group => SplitClusters(group.OrderBy(word => word.Left).ToArray(), dpi))
            .OrderBy(cluster => cluster.Top).ThenBy(cluster => cluster.Left).ToArray();
        var markers = clusters.Where(cluster => IsFromMarker(cluster.Text) || IsToMarker(cluster.Text))
            .OrderBy(cluster => cluster.Left).Take(2).ToArray();
        if (markers.Length == 0) return [];

        var regions = new List<PixelRect>();
        for (var index = 0; index < markers.Length; index++)
        {
            var marker = markers[index];
            var isFromColumn = IsFromMarker(marker.Text);
            var previous = index == 0 ? null : markers[index - 1];
            var next = index + 1 < markers.Length ? markers[index + 1] : null;
            var columnLeft = previous is null ? Math.Max(0, marker.Left - (dpi / 10))
                : (previous.Right + marker.Left) / 2;
            var columnRight = next is null ? pageWidth : (marker.Right + next.Left) / 2;
            var maximumBottom = Math.Min(pageHeight, marker.Top + (int)Math.Round(dpi * 1.35));
            var selected = new List<Cluster> { marker };
            var lastBottom = marker.Bottom;

            foreach (var cluster in clusters
                         .Where(candidate => candidate.Top > marker.Top + Math.Max(2, marker.Height / 3))
                         .Where(candidate => candidate.Top < maximumBottom)
                         .Where(candidate => candidate.CenterX >= columnLeft && candidate.CenterX < columnRight)
                         .OrderBy(candidate => candidate.Top).ThenBy(candidate => candidate.Left))
            {
                if (IsFromMarker(cluster.Text) || IsToMarker(cluster.Text)) continue;
                if (IsStop(cluster.Text, stopAtPhone: !isFromColumn))
                {
                    if (selected.Count > 1) break;
                    continue;
                }
                if (selected.Count > 1 && cluster.Top - lastBottom > Math.Max(20, (int)Math.Round(dpi * 0.20)))
                    break;
                selected.Add(cluster);
                lastBottom = Math.Max(lastBottom, cluster.Bottom);
                if (selected.Count >= 8) break;
            }

            if (selected.Count < 2) continue;
            var left = selected.Min(item => item.Left);
            var top = selected.Min(item => item.Top);
            var right = selected.Max(item => item.Right);
            var bottom = selected.Max(item => item.Bottom);
            var padding = Math.Max(2, (int)Math.Round(dpi * 0.025));
            regions.Add(new PixelRect(left, top, right - left, bottom - top)
                .Inflate(padding, padding, pageWidth, pageHeight));
        }
        return regions;
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
                words.Add(new Word(block, paragraph, line, left, top, width, height, fields[11].Trim()));
        }
        return words;
    }

    private static IEnumerable<Cluster> SplitClusters(IReadOnlyList<Word> ordered, int dpi)
    {
        var current = new List<Word>();
        foreach (var word in ordered)
        {
            if (current.Count > 0)
            {
                var previous = current[^1];
                if (word.Left - previous.Right > Math.Max((int)Math.Round(dpi * 0.23),
                        Math.Max(previous.Height, word.Height) * 4))
                {
                    yield return ToCluster(current);
                    current.Clear();
                }
            }
            current.Add(word);
        }
        if (current.Count > 0) yield return ToCluster(current);
    }

    private static Cluster ToCluster(IReadOnlyList<Word> words)
    {
        var left = words.Min(word => word.Left);
        var top = words.Min(word => word.Top);
        var right = words.Max(word => word.Right);
        var bottom = words.Max(word => word.Bottom);
        return new Cluster(left, top, right - left, bottom - top,
            string.Join(" ", words.Select(word => word.Text)));
    }

    private static bool IsFromMarker(string value)
    {
        var compact = Compact(value);
        return compact is "FROM" or "FROMDE" or "SHIPFROM" or "SENDER";
    }

    private static bool IsToMarker(string value)
    {
        var compact = Compact(value);
        return compact is "TO" or "TOA" or "SHIPTO" or "DELIVERTO" or "CONSIGNEE";
    }

    private static bool IsStop(string value, bool stopAtPhone)
    {
        var normalized = value.ToUpperInvariant();
        return (stopAtPhone && Phone().IsMatch(normalized)) ||
               new[] { "DATE", "WEIGHT", "PIECES", "PUROLATOR PIN", "TRACKING", "REFERENCE", "REF:", "GRD" }
                   .Any(stop => normalized.Contains(stop, StringComparison.Ordinal));
    }

    private static string Compact(string value) =>
        Regex.Replace(value, "[^A-Z]", string.Empty, RegexOptions.IgnoreCase).ToUpperInvariant();

    [GeneratedRegex(@"\b(?:\+?1[ -]?)?\d{3}[ -.\)]*\d{3}[ -.]*\d{4}\b")]
    private static partial Regex Phone();

    private sealed record Word(int Block, int Paragraph, int Line, int Left, int Top, int Width, int Height, string Text)
    {
        public int Right => Left + Width;
        public int Bottom => Top + Height;
    }

    private sealed record Cluster(int Left, int Top, int Width, int Height, string Text)
    {
        public int Right => Left + Width;
        public int Bottom => Top + Height;
        public int CenterX => Left + (Width / 2);
    }
}
