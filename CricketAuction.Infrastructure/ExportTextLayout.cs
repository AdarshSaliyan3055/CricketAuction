using System.Globalization;
using System.Text.RegularExpressions;

namespace CricketAuction.Infrastructure;

public sealed record FittedExportText(string[] Lines, double FontSize, double LineHeight);

public static class ExportTextLayout
{
    public static FittedExportText Fit(string text, double width, double height, double preferredSize,
        double minimumSize, Func<string, double, double> measure)
    {
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        for (var size = preferredSize; ; size = Math.Max(minimumSize, size - .5))
        {
            var lines = Wrap(text, width, size, measure);
            var lineHeight = size * 1.25;
            if (lines.Count * lineHeight <= height && lines.All(line => measure(line, size) <= width))
                return new(lines.ToArray(), size, lineHeight);
            if (size <= minimumSize)
            {
                var count = Math.Max(1, (int)(height / lineHeight));
                var visible = lines.Take(count).ToArray();
                if (visible.Length > 0 && lines.Count > count)
                {
                    var last = visible[^1];
                    while (last.Length > 0 && measure(last + "…", size) > width) last = last[..^1];
                    visible[^1] = last.TrimEnd() + "…";
                }
                return new(visible, size, lineHeight);
            }
        }
    }

    private static List<string> Wrap(string text, double width, double size, Func<string, double, double> measure)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (measure(candidate, size) <= width) { current = candidate; continue; }
            if (current.Length > 0) { lines.Add(current); current = ""; }
            var elements = StringInfo.GetTextElementEnumerator(word);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                if (current.Length > 0 && measure(current + element, size) > width) { lines.Add(current); current = ""; }
                current += element;
            }
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }
}
