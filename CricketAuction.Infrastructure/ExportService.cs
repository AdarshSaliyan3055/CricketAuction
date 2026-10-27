using ClosedXML.Excel;
using CricketAuction.Domain;

namespace CricketAuction.Infrastructure;
public sealed class ExportService(Workspace workspace, AuctionService auction, AppLog log) : IExportService
{
    public void Export(string path, IReadOnlyList<Player> players, bool complete)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Players");
        string[] headers = ["Player ID", "Player", "Phone", "Age", "Player Type", "Batting", "Bowling", "City", "Jersey Name", "Jersey Number", "Jersey Size", "Status", "Round", "Sold Amount (INR)", "Sold To", "Sold At (UTC)", "Notes", "Registration Timestamp", "Photo URL", "Original Registration"];
        Header(sheet, headers);
        var r = 2;
        foreach (var p in players)
        {
            object?[] values = [p.Id, p.FullName, p.PhoneNumber, p.Age, p.PlayerType, p.BattingStyle, p.BowlingStyle, p.AreaCity, p.JerseyName, p.JerseyNumber, p.JerseySize, p.Status.ToString(), p.AuctionRound, p.SoldPaise / 100m, p.TeamName, p.SoldAt?.ToString("O"), p.Notes, p.RegistrationTimestamp, p.PlayerPhotoUrl, p.OriginalRegistrationJson];
            WriteRow(sheet, r++, values);
        }
        sheet.Column(14).Style.NumberFormat.Format = "₹ #,##0.00"; Finish(sheet);
        if (complete)
        {
            var history = book.AddWorksheet("Auction History");
            Header(history, ["Event ID", "Player ID", "Player", "Round", "Action", "Status", "Amount (INR)", "Team", "Timestamp (UTC)", "Notes", "Action ID", "Reverses Action", "Previous State"]);
            r = 2;
            foreach (var h in auction.History()) WriteRow(history, r++, [h.Id, h.PlayerId, h.Player?.FullName, h.AuctionRound, h.Action, h.Status.ToString(), h.SoldPaise / 100m, h.TeamName, h.Timestamp.ToString("O"), h.Notes, h.ActionId.ToString(), h.ReversesActionId?.ToString(), h.BeforeState]);
            Finish(history);
            var teams = book.AddWorksheet("Teams"); Header(teams, ["Team", "Owner name", "Budget (INR)", "Spent (INR)", "Remaining (INR)", "Players"]); r = 2;
            foreach (var t in auction.Teams()) WriteRow(teams, r++, [t.Name, t.OwnerName, t.BudgetPaise / 100m, t.SpentPaise / 100m, (t.BudgetPaise - t.SpentPaise) / 100m, t.PlayerCount]);
            Finish(teams);
            var purchases = book.AddWorksheet("Team Purchases");
            Header(purchases, ["Team", "Owner name", "Player", "Age", "Phone", "Player Type", "City", "Batting", "Bowling", "Bought For (INR)", "Round", "Sold At (UTC)", "Notes"]);
            var soldPlayers = auction.Players().Where(p => p.Status == AuctionStatus.Sold).ToList(); r = 2;
            foreach (var team in auction.Teams().OrderBy(t => t.Name))
            {
                var members = soldPlayers.Where(p => p.TeamId == team.Id).OrderBy(p => p.FullName).ToList();
                foreach (var p in members)
                {
                    WriteRow(purchases, r, [team.Name, team.OwnerName, p.FullName, p.Age, p.PhoneNumber, p.PlayerType, p.AreaCity, p.BattingStyle, p.BowlingStyle, p.SoldPaise / 100m, p.AuctionRound, p.SoldAt?.ToString("O"), p.Notes]);
                    if (r % 2 == 0) purchases.Row(r).Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F7F3");
                    r++;
                }
                if (members.Count > 0)
                {
                    WriteRow(purchases, r, [team.Name, "TEAM TOTAL", "", "", "", "", "", "", "", members.Sum(p => p.SoldPaise) / 100m, "", "", $"{members.Count} players"]);
                    purchases.Row(r).Style.Fill.BackgroundColor = XLColor.FromHtml("#DCECE2"); purchases.Row(r).Style.Font.Bold = true; r++;
                }
            }
            WriteRow(purchases, r, ["ALL TEAMS", "AUCTION TOTAL", "", "", "", "", "", "", "", soldPlayers.Sum(p => p.SoldPaise) / 100m, "", "", $"{soldPlayers.Count} players"]);
            purchases.Row(r).Style.Fill.BackgroundColor = XLColor.FromHtml("#12382F"); purchases.Row(r).Style.Font.FontColor = XLColor.White; purchases.Row(r).Style.Font.Bold = true;
            purchases.Column(10).Style.NumberFormat.Format = "₹ #,##0.00"; Finish(purchases);
            var summary = book.AddWorksheet("Summary"); var stats = Statistics.Calculate(auction.Players()); var session = workspace.Session();
            Header(summary, [session.AuctionName, session.TournamentName]);
            object?[][] rows = [["Current round", session.CurrentRound], ["Total registered", stats.Total], ["Sold", stats.Sold], ["Unsold", stats.Unsold], ["Available", stats.Available], ["Total auction value (INR)", stats.TotalPaise / 100m], ["Average sold price (INR)", stats.AveragePaise / 100m], ["Highest sold price (INR)", stats.HighestPaise / 100m], ["Highest sold player", auction.Players().Where(p => p.Status == AuctionStatus.Sold).OrderByDescending(p => p.SoldPaise).FirstOrDefault()?.FullName ?? "—"]];
            r = 2; foreach (var row in rows) WriteRow(summary, r++, row); Finish(summary);
        }
        AtomicFile.Write(path, temp => book.SaveAs(temp)); log.Write($"Excel export count={players.Count}");
    }
    private static void Header(IXLWorksheet sheet, string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        sheet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#12382F"); sheet.Row(1).Style.Font.FontColor = XLColor.White; sheet.Row(1).Style.Font.Bold = true;
    }
    private static void WriteRow(IXLWorksheet sheet, int row, object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var cell = sheet.Cell(row, i + 1);
            // Explicit text assignment prevents formula execution from registration data.
            cell.Value = values[i] switch { int n => n, long n => n, decimal n => (double)n, null => "", _ => values[i]!.ToString() ?? "" };
        }
    }
    private static void Finish(IXLWorksheet sheet) { sheet.SheetView.FreezeRows(1); sheet.RangeUsed()?.SetAutoFilter(); sheet.Columns().AdjustToContents(8, 45); }
}
public static class AtomicFile
{
    public static void Write(string path, Action<string> write)
    {
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Guid.NewGuid()}{Path.GetExtension(path)}");
        try { write(temp); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
