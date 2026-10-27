using CricketAuction.Domain;
using PdfSharpCore;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace CricketAuction.Infrastructure;
public sealed class TeamReportPdfService(AppLog log)
{
    private static readonly XColor Forest = XColor.FromArgb(8, 31, 27);
    private static readonly XColor Green = XColor.FromArgb(22, 121, 82);
    private static readonly XColor Gold = XColor.FromArgb(230, 182, 75);
    private static readonly XColor Pale = XColor.FromArgb(240, 246, 242);
    private const double Margin = 38;
    private const double RowHeight = 82;
    private const int RowsPerPage = 7;

    public void Generate(string path, IReadOnlyList<Team> teams, IReadOnlyList<Player> players, string tournament)
    {
        if (!players.Any(player => player.Status == AuctionStatus.Sold))
            throw new InvalidOperationException("There are no purchased players to include in the team report.");

        using var document = new PdfDocument();
        document.Info.Title = $"{tournament} - Team Purchases";
        var sold = players.Where(player => player.Status == AuctionStatus.Sold).ToList();
        var pageNumber = 0;
        foreach (var team in teams.OrderBy(team => team.Name))
        {
            var purchases = sold.Where(player => player.TeamId == team.Id).OrderBy(player => player.FullName).ToList();
            var pageCount = Math.Max(1, (int)Math.Ceiling(purchases.Count / (double)RowsPerPage));
            for (var chunk = 0; chunk < pageCount; chunk++)
            {
                var page = document.AddPage(); page.Size = PageSize.A4; page.Orientation = PageOrientation.Portrait;
                using var graphics = XGraphics.FromPdfPage(page);
                DrawPage(graphics, page, tournament, team, purchases.Skip(chunk * RowsPerPage).Take(RowsPerPage).ToList(), chunk, pageCount, ++pageNumber);
            }
        }

        AtomicFile.Write(path, temporary => document.Save(temporary));
        log.Write($"Team purchase PDF export pages={pageNumber}");
    }

    private static void DrawPage(XGraphics graphics, PdfPage page, string tournament, Team team, List<Player> players, int chunk, int pageCount, int pageNumber)
    {
        var width = page.Width.Point;
        graphics.DrawRectangle(new XSolidBrush(Forest), 0, 0, width, 168);
        graphics.DrawRectangle(new XSolidBrush(Gold), 0, 0, width, 7);
        DrawText(graphics, tournament.ToUpperInvariant(), new XFont("Arial", 11, XFontStyle.Bold), new XSolidBrush(XColor.FromArgb(124, 226, 189)), new XRect(Margin, 27, width - Margin * 2, 18), XStringFormats.TopLeft);
        DrawText(graphics, chunk == 0 ? team.Name : $"{team.Name} (CONTINUED)", new XFont("Arial", 26, XFontStyle.Bold), XBrushes.White, new XRect(Margin, 52, width - Margin * 2, 38), XStringFormats.TopLeft);
        var countLabel = $"{players.Count} PLAYERS SHOWN";
        DrawText(graphics, countLabel, new XFont("Arial", 10, XFontStyle.Bold), new XSolidBrush(XColor.FromArgb(192, 215, 207)), new XRect(Margin, 99, 230, 18), XStringFormats.TopLeft);
        if (chunk == 0)
        {
            var teamPlayers = team.PlayerCount;
            var summary = $"{team.Spent} SPENT    |    {team.Budget} BUDGET    |    {teamPlayers} PLAYERS";
            DrawText(graphics, summary, new XFont("Arial", 10, XFontStyle.Bold), new XSolidBrush(Gold), new XRect(Margin, 126, width - Margin * 2, 18), XStringFormats.TopLeft);
        }
        else
        {
            DrawText(graphics, $"PAGE {chunk + 1} OF {pageCount}", new XFont("Arial", 10, XFontStyle.Bold), new XSolidBrush(Gold), new XRect(Margin, 126, width - Margin * 2, 18), XStringFormats.TopLeft);
        }

        var y = 185d;
        foreach (var player in players)
        {
            graphics.DrawRectangle(new XSolidBrush(Pale), Margin, y, width - Margin * 2, RowHeight - 7);
            var photoBox = new XRect(Margin + 9, y + 8, 52, 60);
            if (player.Photo is { Length: > 8 } bytes && ImageService.IsImage(bytes))
            {
                try
                {
                    using var image = XImage.FromStream(() => new MemoryStream(bytes));
                    var scale = Math.Min(photoBox.Width / image.PixelWidth, photoBox.Height / image.PixelHeight);
                    var drawWidth = image.PixelWidth * scale; var drawHeight = image.PixelHeight * scale;
                    graphics.DrawImage(image, photoBox.X + (photoBox.Width - drawWidth) / 2, photoBox.Y + (photoBox.Height - drawHeight) / 2, drawWidth, drawHeight);
                }
                catch (Exception) { DrawPhotoPlaceholder(graphics, photoBox); }
            }
            else DrawPhotoPlaceholder(graphics, photoBox);

            var textX = Margin + 76;
            var priceX = width - Margin - 125;
            var textWidth = priceX - textX - 16;
            DrawText(graphics, player.FullName, new XFont("Arial", 14, XFontStyle.Bold), new XSolidBrush(Forest), new XRect(textX, y + 7, textWidth, 27), XStringFormats.TopLeft);
            var details = string.Join("  |  ", new[] { player.PlayerType, player.AreaCity, $"Round {player.AuctionRound}" }.Where(value => !string.IsNullOrWhiteSpace(value)));
            DrawText(graphics, details, new XFont("Arial", 9, XFontStyle.Regular), new XSolidBrush(Green), new XRect(textX, y + 37, textWidth, 32), XStringFormats.TopLeft);
            DrawText(graphics, "BOUGHT FOR", new XFont("Arial", 8, XFontStyle.Bold), new XSolidBrush(XColor.FromArgb(99, 120, 111)), new XRect(width - Margin - 130, y + 13, 118, 14), XStringFormats.TopRight);
            DrawText(graphics, Money.Format(player.SoldPaise), new XFont("Arial", 15, XFontStyle.Bold), new XSolidBrush(Green), new XRect(priceX, y + 32, 113, 28), XStringFormats.TopRight);
            y += RowHeight;
        }

        graphics.DrawLine(new XPen(XColor.FromArgb(220, 230, 223), 1), Margin, page.Height.Point - 47, width - Margin, page.Height.Point - 47);
        DrawText(graphics, $"TEAM PURCHASE REPORT  •  {tournament}", new XFont("Arial", 8, XFontStyle.Regular), new XSolidBrush(XColor.FromArgb(99, 120, 111)), new XRect(Margin, page.Height.Point - 38, width - Margin * 2 - 42, 14), XStringFormats.TopLeft);
        DrawText(graphics, pageNumber.ToString(), new XFont("Arial", 8, XFontStyle.Bold), new XSolidBrush(Green), new XRect(width - Margin - 30, page.Height.Point - 38, 30, 14), XStringFormats.TopRight);
    }

    private static void DrawText(XGraphics graphics, string text, XFont font, XBrush brush, XRect box, XStringFormat alignment)
    {
        var layout = ExportTextLayout.Fit(text, box.Width - 2, box.Height, font.Size, Math.Min(8, font.Size),
            (value, size) => graphics.MeasureString(value, new XFont(font.Name, size, font.Style)).Width);
        var fittedFont = new XFont(font.Name, layout.FontSize, font.Style);
        var format = alignment.Alignment == XStringAlignment.Far ? XStringFormats.TopRight
            : alignment.Alignment == XStringAlignment.Center ? XStringFormats.TopCenter : XStringFormats.TopLeft;
        var state = graphics.Save();
        graphics.IntersectClip(box);
        for (var i = 0; i < layout.Lines.Length; i++)
            graphics.DrawString(layout.Lines[i], fittedFont, brush,
                new XRect(box.X, box.Y + i * layout.LineHeight, box.Width, layout.LineHeight), format);
        graphics.Restore(state);
    }

    private static void DrawPhotoPlaceholder(XGraphics graphics, XRect box)
    {
        graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(220, 230, 223)), box);
        DrawText(graphics, "PHOTO", new XFont("Arial", 7, XFontStyle.Bold), new XSolidBrush(Green), box, XStringFormats.Center);
    }
}
