using CricketAuction.Infrastructure;
using PdfSharpCore.Drawing;

namespace CricketAuction.Tests;

public class ExportTextLayoutTests
{
    [Theory]
    [InlineData("Neelavara Cross, Arooru village", 267, 46, 26)]
    [InlineData("A player with a particularly long display name", 253, 64, 24)]
    [InlineData("All-rounder | Neelavara Cross, Arooru village | Round 12", 300, 32, 9)]
    [InlineData("AnExtremelyLongUnbrokenCityNameThatMustWrapWithoutOverflow", 110, 80, 20)]
    public void LongTextFitsAndRetainsAllContent(string text, double width, double height, double size)
    {
        using var graphics = XGraphics.CreateMeasureContext(new XSize(1000, 1000), XGraphicsUnit.Point, XPageDirection.Downwards);
        double Measure(string value, double fontSize) => graphics.MeasureString(value, new XFont("Arial", fontSize, XFontStyle.Bold)).Width;
        var layout = ExportTextLayout.Fit(text, width, height, size, 8, Measure);
        Assert.True(layout.Lines.Length * layout.LineHeight <= height);
        Assert.All(layout.Lines, line => Assert.True(Measure(line, layout.FontSize) <= width));
        Assert.Equal(text.Replace(" ", ""), string.Concat(layout.Lines).Replace(" ", ""));
    }
}
