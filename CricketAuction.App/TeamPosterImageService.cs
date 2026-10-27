using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CricketAuction.Domain;
using CricketAuction.Infrastructure;

namespace CricketAuction.App;

public enum TeamPosterDesign { ClassicGreen, StadiumBlue, CleanCards, MidnightGold, CrimsonSpotlight }

public sealed class TeamPosterImageService(AppLog log)
{
    private const int Width = 1600;
    private const int Margin = 52;
    private const int HeaderHeight = 310;
    private sealed record Palette(Color Background, Color Header, Color Accent, Color Card, Color Ink);
    private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static Palette ColorsFor(TeamPosterDesign design) => design switch
    {
        TeamPosterDesign.ClassicGreen => new(Hex("#ECF4EF"), Hex("#081F1B"), Hex("#EFBE49"), Hex("#053F36"), Colors.White),
        TeamPosterDesign.StadiumBlue => new(Hex("#061C53"), Hex("#031235"), Hex("#36E4F4"), Hex("#0649A1"), Colors.White),
        TeamPosterDesign.CleanCards => new(Hex("#E8EBEF"), Hex("#FFFFFF"), Hex("#007AA3"), Colors.White, Hex("#19334A")),
        TeamPosterDesign.MidnightGold => new(Hex("#12141C"), Hex("#090A10"), Hex("#EFC974"), Hex("#222633"), Colors.White),
        TeamPosterDesign.CrimsonSpotlight => new(Hex("#28101D"), Hex("#150913"), Hex("#FF5575"), Hex("#45182C"), Colors.White),
        _ => throw new ArgumentOutOfRangeException(nameof(design))
    };
    public static string DesignName(TeamPosterDesign design) => design switch
    {
        TeamPosterDesign.ClassicGreen => "Classic Green",
        TeamPosterDesign.StadiumBlue => "Stadium Blue",
        TeamPosterDesign.CleanCards => "Clean Cards",
        TeamPosterDesign.MidnightGold => "Midnight Gold",
        TeamPosterDesign.CrimsonSpotlight => "Crimson Spotlight",
        _ => throw new ArgumentOutOfRangeException(nameof(design))
    };

    public void Generate(string path, Team team, IReadOnlyList<Player> players, string tournament,
        TeamPosterDesign design = TeamPosterDesign.ClassicGreen, string auctionName = "")
    {
        var bitmap = Render(team, players, tournament, design, auctionName);
        AtomicFile.Write(path, temporary =>
        {
            using var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
        });
        log.Write($"Team poster image export design={design}");
    }

    public BitmapSource Render(Team team, IReadOnlyList<Player> players, string tournament,
        TeamPosterDesign design, string auctionName = "", int pixelWidth = Width)
    {
        if (pixelWidth < 1 || pixelWidth > Width) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        var purchases = players.Where(player => player.Status == AuctionStatus.Sold && player.TeamId == team.Id)
            .OrderBy(player => player.FullName).ToList();
        if (purchases.Count == 0) throw new InvalidOperationException($"{team.Name} has not bought any players yet.");
        var palette = ColorsFor(design);
        var columns = design is TeamPosterDesign.StadiumBlue or TeamPosterDesign.CrimsonSpotlight ? 4 : 5;
        const double gap = 22;
        var cardWidth = (Width - 2 * Margin - (columns - 1) * gap) / columns;
        var cardHeight = cardWidth + 124;
        var rowCount = (purchases.Count + columns - 1) / columns;
        var height = HeaderHeight + 30 + rowCount * (cardHeight + gap) + 70;
        if (height > 20_000) throw new InvalidOperationException("This roster is too large for a single poster image. Use the Team report PDF for a multi-page report.");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(palette.Background), null, new Rect(0, 0, Width, height));
            if (design == TeamPosterDesign.StadiumBlue)
            {
                dc.DrawEllipse(new RadialGradientBrush(Hex("#087FD0"), palette.Background), null,
                    new Point(Width / 2, height / 2), Width, height / 2);
                for (var i = 0; i < 7; i++)
                    dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(28, 120, 230, 255)), 4),
                        new Point(Width / 2, height / 2), 250 + i * 170, height * .18 + i * 100);
            }
            dc.DrawRectangle(new SolidColorBrush(palette.Header), null, new Rect(0, 0, Width, HeaderHeight));
            dc.DrawRectangle(new SolidColorBrush(palette.Accent), null, new Rect(0, 0, Width, 10));
            var headerInk = design == TeamPosterDesign.CleanCards ? palette.Ink : Colors.White;
            var alignment = design is TeamPosterDesign.ClassicGreen or TeamPosterDesign.CleanCards ? TextAlignment.Left : TextAlignment.Center;
            var hasLogo = team.Logo is { Length: > 8 };
            var headerX = hasLogo ? Margin + 210 : Margin;
            var headerWidth = Width - Margin - headerX;
            if (hasLogo) DrawPhoto(dc, team.Logo, new Rect(Margin, 44, 175, 175), palette.Accent);
            Text(dc, tournament, headerX, 34, 36, headerInk, headerWidth, 58, alignment);
            Text(dc, team.Name.ToUpperInvariant(), headerX, 105, 72, headerInk, headerWidth, 100, alignment);
            Text(dc, string.IsNullOrWhiteSpace(team.OwnerName) ? "OFFICIAL SQUAD" : "OWNER: " + team.OwnerName,
                headerX, 215, 22, palette.Accent, headerWidth, 44, alignment);
            Text(dc, auctionName, Margin, 263, 20, headerInk, Width - 2 * Margin, 32, alignment);
            dc.DrawRectangle(new SolidColorBrush(palette.Accent), null, new Rect(Margin, HeaderHeight - 3, Width - 2 * Margin, 3));

            for (var index = 0; index < purchases.Count; index++)
            {
                var player = purchases[index];
                var row = index / columns;
                var inRow = Math.Min(columns, purchases.Count - row * columns);
                var rowWidth = inRow * cardWidth + (inRow - 1) * gap;
                var x = (Width - rowWidth) / 2 + index % columns * (cardWidth + gap);
                var y = HeaderHeight + 30 + row * (cardHeight + gap);
                var radius = design == TeamPosterDesign.CleanCards ? 0 : 16;
                dc.DrawRoundedRectangle(new SolidColorBrush(palette.Card), new Pen(new SolidColorBrush(palette.Accent), 2),
                    new Rect(x, y, cardWidth, cardHeight), radius, radius);
                var photo = new Rect(x + 12, y + 12, cardWidth - 24, cardWidth - 24);
                if (design == TeamPosterDesign.CleanCards)
                {
                    Text(dc, player.PlayerType, x + 10, y + 10, 17, palette.Ink, cardWidth - 20, 27);
                    photo = new Rect(x + 12, y + 42, cardWidth - 24, cardWidth - 54);
                }
                DrawPhoto(dc, player.Photo, photo, palette.Accent);
                if (design is TeamPosterDesign.CleanCards or TeamPosterDesign.CrimsonSpotlight)
                {
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        context.BeginFigure(new Point(x + 1, y + cardWidth + 10), true, true);
                        context.LineTo(new Point(x + cardWidth - 1, y + cardWidth - 17), true, false);
                        context.LineTo(new Point(x + cardWidth - 1, y + cardHeight - 1), true, false);
                        context.LineTo(new Point(x + 1, y + cardHeight - 1), true, false);
                    }
                    dc.DrawGeometry(new SolidColorBrush(palette.Accent), null, geometry);
                }
                var nameInk = design == TeamPosterDesign.CleanCards ? Colors.White : palette.Ink;
                Text(dc, player.FullName, x + 14, y + cardWidth + 14, 24, nameInk, cardWidth - 28, 64);
                if (design != TeamPosterDesign.CleanCards)
                    Text(dc, player.PlayerType, x + 14, y + cardWidth + 86, 17, palette.Ink, cardWidth - 28, 30);
            }
            var footerInk = design == TeamPosterDesign.ClassicGreen ? palette.Header : palette.Ink;
            dc.DrawRectangle(new SolidColorBrush(palette.Accent), null, new Rect(Margin, height - 56, Width - 2 * Margin, 2));
            Text(dc, tournament, Margin, height - 42, 18, footerInk, 1100, 30, TextAlignment.Left);
            Text(dc, $"{purchases.Count} PLAYERS", Width - Margin - 300, height - 42, 18, footerInk, 300, 30, TextAlignment.Right);
        }
        var scale = pixelWidth / (double)Width;
        var scaled = new DrawingVisual();
        using (var dc = scaled.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(visual.Drawing);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(pixelWidth, (int)Math.Ceiling(height * scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(scaled); bitmap.Freeze(); return bitmap;
    }

    private static void DrawPhoto(DrawingContext dc, byte[]? bytes, Rect rect, Color accent)
    {
        dc.DrawRoundedRectangle(new LinearGradientBrush(Hex("#E8EFF4"), accent, 90), null, rect, 8, 8);
        if (bytes is { Length: > 8 } && ImageService.IsImage(bytes))
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 500; image.StreamSource = stream; image.EndInit(); image.Freeze();
                var scale = Math.Min(rect.Width / image.PixelWidth, rect.Height / image.PixelHeight);
                var width = image.PixelWidth * scale; var height = image.PixelHeight * scale;
                dc.DrawImage(image, new Rect(rect.X + (rect.Width - width) / 2, rect.Y + (rect.Height - height) / 2, width, height));
                return;
            }
            catch (Exception) { /* Unreadable photos use the same silhouette as missing photos. */ }
        }
        dc.PushClip(new RectangleGeometry(rect, 8, 8));
        var silhouette = new SolidColorBrush(Hex("#526879"));
        dc.DrawEllipse(silhouette, null, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height * .32), rect.Width * .17, rect.Height * .2);
        dc.DrawEllipse(silhouette, null, new Point(rect.X + rect.Width / 2, rect.Bottom), rect.Width * .39, rect.Height * .43);
        dc.Pop();
    }

    private static void Text(DrawingContext dc, string text, double x, double y, double size, Color color,
        double maxWidth, double maxHeight, TextAlignment alignment = TextAlignment.Center)
    {
        FormattedText Format(string value, double fontSize) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            fontSize, new SolidColorBrush(color), 1.0);
        var layout = ExportTextLayout.Fit(text, maxWidth - 4, maxHeight, size, Math.Min(size, 14),
            (value, fontSize) => Format(value, fontSize).WidthIncludingTrailingWhitespace);
        dc.PushClip(new RectangleGeometry(new Rect(x, y, maxWidth, maxHeight)));
        for (var i = 0; i < layout.Lines.Length; i++)
        {
            var formatted = Format(layout.Lines[i], layout.FontSize);
            formatted.MaxTextWidth = maxWidth;
            formatted.TextAlignment = alignment;
            dc.DrawText(formatted, new Point(x, y + i * layout.LineHeight));
        }
        dc.Pop();
    }
}
