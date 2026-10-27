using ClosedXML.Excel;
using CricketAuction.Domain;
using CricketAuction.Infrastructure;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.EntityFrameworkCore;
using PdfSharpCore.Pdf.IO;

namespace CricketAuction.Tests;
public sealed class AuctionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CricketAuctionTests", Guid.NewGuid().ToString());
    private readonly Workspace workspace;
    private readonly AuctionService service;
    private readonly AppLog log;
    public AuctionTests() { workspace = new(root); workspace.Create("Test Tournament", "Test Auction"); log = new(workspace); service = new(workspace, log); }
    [Fact] public void WorkspaceListsSavedAuctions()
    {
        workspace.Create("Second Tournament", "Second Auction");
        var auctions = workspace.Auctions();
        Assert.Equal(2, auctions.Count);
        Assert.Contains(auctions, item => item.TournamentName == "Test Tournament" && item.AuctionName == "Test Auction");
        Assert.Contains(auctions, item => item.TournamentName == "Second Tournament" && item.AuctionName == "Second Auction");
    }
    [Fact] public void NewAuctionRequiresAndEnforcesTheConfiguredTeamBudget()
    {
        var budgetRoot = Path.Combine(root, "budgeted"); var budgetWorkspace = new Workspace(budgetRoot);
        Assert.Throws<InvalidOperationException>(() => budgetWorkspace.Create("Budget Cup", "Auction", teamNames: ["Team A"]));
        budgetWorkspace.Create("Budget Cup", "Auction", teamNames: ["Team A", "Team B"], teamBudgetPaise: 250_000);
        Assert.True(budgetWorkspace.Session().EnableBudgets); Assert.Equal(250_000, budgetWorkspace.Session().DefaultBudgetPaise);
        var budgetAuction = new AuctionService(budgetWorkspace, log); var teams = budgetAuction.Teams(); Assert.All(teams, team => Assert.Equal(250_000, team.BudgetPaise));
        var player = new Player { FullName = "Budget Player", PhoneNumber = "9000000001" }; budgetAuction.SavePlayer(player);
        Assert.Throws<InvalidOperationException>(() => budgetAuction.Sell(player.Id, teams[0].Id, 250_001, ""));
        budgetAuction.Sell(player.Id, teams[0].Id, 100_000, "");
        var updatedTeam = budgetAuction.Teams().Single(team => team.Id == teams[0].Id);
        Assert.Equal(150_000L, updatedTeam.BudgetPaise!.Value - updatedTeam.SpentPaise);
    }
    [Fact] public void SavedAuctionsCanBeRenamedAndDeletedSafely()
    {
        var originalPath = workspace.DatabasePath;
        workspace.Create("Second Tournament", "Second Auction"); var secondPath = workspace.DatabasePath;
        workspace.UpdateAuctionNames(originalPath, "Updated Tournament", "Updated Auction");
        Assert.Contains(workspace.Auctions(), item => item.DatabasePath == originalPath && item.TournamentName == "Updated Tournament" && item.AuctionName == "Updated Auction");
        workspace.Create("Third Tournament", "Third Auction"); var thirdPath = workspace.DatabasePath;
        workspace.DeleteAuction(originalPath);
        Assert.DoesNotContain(workspace.Auctions(), item => item.DatabasePath == originalPath);
        Assert.Equal(thirdPath, workspace.DatabasePath);
        workspace.DeleteAuction(thirdPath);
        Assert.Equal(secondPath, workspace.DatabasePath);
        workspace.DeleteAuction(secondPath);
        Assert.Empty(workspace.Auctions()); Assert.Equal("", workspace.DatabasePath);
        Assert.False(File.Exists(Path.Combine(root, "last-auction.txt")));
    }
    [Fact] public void SavedAuctionCanBeDeletedWhenNoneIsActive()
    {
        var idleRoot = Path.Combine(root, "idle"); var creator = new Workspace(idleRoot); creator.Create("Saved Tournament", "Saved Auction");
        File.Delete(Path.Combine(idleRoot, "last-auction.txt")); var idleWorkspace = new Workspace(idleRoot);
        Assert.Equal("", idleWorkspace.DatabasePath); idleWorkspace.DeleteAuction(creator.DatabasePath); Assert.Empty(idleWorkspace.Auctions());
    }
    [Fact] public void AuctionAndTeamNamesSaveTogetherWithoutChangingPurchasesOrHistory()
    {
        var first = Team();
        var second = new Team { Name = "Second Team", BudgetPaise = 1000000 }; service.SaveTeam(second);
        var player = Player(); service.Sell(player.Id, first.Id, 250000, "Sale");
        var history = System.Text.Json.JsonSerializer.Serialize(service.History(player.Id));
        var path = workspace.DatabasePath;
        workspace.Create("Other", "Other"); var otherPath = workspace.DatabasePath;
        workspace.UpdateAuctionNames(path, "Renamed tournament", "Renamed auction",
            new Dictionary<int, string> { [first.Id] = second.Name, [second.Id] = first.Name });
        Assert.Equal(otherPath, workspace.DatabasePath);
        workspace.Select(path);
        Assert.Equal("Renamed auction", workspace.Session().AuctionName);
        var saved = service.Players().Single();
        Assert.Equal(first.Id, saved.TeamId); Assert.Equal(second.Name, saved.TeamName); Assert.Equal(250000, saved.SoldPaise);
        Assert.Equal(history, System.Text.Json.JsonSerializer.Serialize(service.History(player.Id)));
        Assert.Equal(1000000, service.Teams().Single(t => t.Id == second.Id).BudgetPaise);
    }
    [Fact] public void InvalidTeamRenameLeavesAllNamesUnchanged()
    {
        var first = Team(); var second = new Team { Name = "Second Team" }; service.SaveTeam(second);
        var original = workspace.Session().AuctionName;
        Assert.Throws<InvalidOperationException>(() => workspace.UpdateAuctionNames(workspace.DatabasePath, "Changed", "Changed",
            new Dictionary<int, string> { [first.Id] = "Same", [second.Id] = " same " }));
        Assert.Equal(original, workspace.Session().AuctionName);
        Assert.Equal(first.Name, service.Teams().Single(t => t.Id == first.Id).Name);
        Assert.Throws<InvalidOperationException>(() => workspace.UpdateAuctionNames(workspace.DatabasePath, "Changed", "Changed",
            new Dictionary<int, string> { [first.Id] = "New" }));
        Assert.Equal(original, workspace.Session().AuctionName);
    }
    [Fact] public void RotatedPhotoCanBeResetAfterReopeningDatabase()
    {
        var player = Player(); byte[] original = [1, 2, 3, 4]; byte[] rotated = [4, 3, 2, 1];
        service.SavePhoto(player.Id, original, true); service.SaveRotatedPhoto(player.Id, original, rotated);
        var reopened = new AuctionService(new Workspace(root), log);
        Assert.True(reopened.HasOriginalPhoto(player.Id)); Assert.Equal(rotated, reopened.Players().Single().Photo);
        Assert.True(reopened.RestoreOriginalPhoto(player.Id)); Assert.Equal(original, reopened.Players().Single().Photo);
        Assert.False(reopened.HasOriginalPhoto(player.Id));
    }
    private Player Player(string name = "Adarsh Saliyan", string phone = "8296419486", string jersey = "96") { var p = new Player { FullName = name, PhoneNumber = phone, Age = 24, JerseyName = "SONU", JerseyNumber = jersey, PlayerType = "Batsman", BattingStyle = "Right Hand", BowlingStyle = "Don't Bowl", AreaCity = "Aroor", JerseySize = "XL" }; service.SavePlayer(p); return p; }
    private Team Team(long? budget = 2_000_000) { var t = new Team { Name = "Team A", BudgetPaise = budget }; service.SaveTeam(t); return t; }
    [Fact] public void SalePersistsWithHistoryAndStatistics()
    {
        var p = Player(); var t = Team(); service.Sell(p.Id, t.Id, 250_000, "Opening sale");
        var saved = Assert.Single(service.Players()); Assert.Equal(AuctionStatus.Sold, saved.Status); Assert.Equal(250_000, saved.SoldPaise); Assert.Equal(t.Id, saved.TeamId);
        Assert.Single(service.History()); Assert.Equal(250_000, Assert.Single(service.Teams()).SpentPaise);
        Assert.Equal(250_000, Statistics.Calculate(service.Players()).TotalPaise);
        var reopened = new Workspace(root); Assert.Equal(AuctionStatus.Sold, new AuctionService(reopened, log).Players()[0].Status);
    }
    [Theory] [InlineData(50000, true)] [InlineData(100000, true)] [InlineData(100001, false)]
    public void BudgetBoundary(long amount, bool succeeds)
    {
        var p = Player(); var team = Team(100000);
        if (succeeds) service.Sell(p.Id, team.Id, amount, "");
        else { Assert.Throws<InvalidOperationException>(() => service.Sell(p.Id, team.Id, amount, "")); Assert.Equal(AuctionStatus.Available, service.Players()[0].Status); Assert.Empty(service.History()); }
    }
    [Fact] public void BudgetIncludesExistingSales()
    {
        var p = Player(); var q = Player("Nithesh", "7899984478", "89"); var t = Team(400000);
        service.Sell(p.Id, t.Id, 250000, ""); Assert.Throws<InvalidOperationException>(() => service.Sell(q.Id, t.Id, 200000, ""));
        service.Sell(q.Id, t.Id, 150000, ""); Assert.Equal("₹0", service.Teams()[0].Remaining);
    }
    [Fact] public void DisabledBudgetsAllowSale()
    {
        var settings = workspace.Session(); settings.EnableBudgets = false; workspace.SaveSettings(settings);
        var p = Player(); var t = Team(100); service.Sell(p.Id, t.Id, 250000, ""); Assert.Equal(AuctionStatus.Sold, service.Players()[0].Status);
    }
    [Fact] public void ReauctionPreservesHistoryAcrossRoundsAndUndo()
    {
        var p = Player(); var t = Team(); service.MarkUnsold(p.Id); Assert.Equal(AuctionStatus.Unsold, service.Players()[0].Status);
        Assert.Equal(1, service.StartNextRound()); Assert.Equal(2, workspace.Session().CurrentRound); Assert.Equal(AuctionStatus.Available, service.Players()[0].Status);
        service.Sell(p.Id, t.Id, 300000, "Round two"); Assert.Equal(3, service.History().Count); Assert.Contains(service.History(), h => h.AuctionRound == 1 && h.Status == AuctionStatus.Unsold);
        service.Undo(); Assert.Equal(AuctionStatus.Available, service.Players()[0].Status); Assert.Equal(0, service.Teams()[0].SpentPaise);
        service.Undo(); Assert.Equal(1, workspace.Session().CurrentRound); Assert.Equal(AuctionStatus.Unsold, service.Players()[0].Status);
        service.Undo(); Assert.Equal(AuctionStatus.Available, service.Players()[0].Status); Assert.Equal(6, service.History().Count);
        Assert.Throws<InvalidOperationException>(() => service.Undo());
    }
    [Fact] public void RoundIsAtomicAcrossMultiplePlayers()
    {
        var p = Player(); var q = Player("Other", "9123456789", "10"); service.MarkUnsold(p.Id);
        Assert.Throws<InvalidOperationException>(() => service.StartNextRound()); service.MarkUnsold(q.Id); service.StartNextRound(); service.Undo();
        Assert.All(service.Players(), p => Assert.Equal(AuctionStatus.Unsold, p.Status)); Assert.Equal(1, workspace.Session().CurrentRound);
    }
    [Fact] public void CannotSellTwiceOrChangeHistoryByEditing()
    {
        var p = Player(); var t = Team(); service.Sell(p.Id, t.Id, 50000, "");
        Assert.Throws<InvalidOperationException>(() => service.Sell(p.Id, t.Id, 50000, "")); Assert.Throws<InvalidOperationException>(() => service.MarkUnsold(p.Id));
        p.FullName = "Updated name"; p.Status = AuctionStatus.Available; service.SavePlayer(p);
        Assert.Equal(AuctionStatus.Sold, service.Players()[0].Status); Assert.Single(service.History());
        using var db = workspace.Open(); Assert.ThrowsAny<Exception>(() => db.Database.ExecuteSqlRaw("UPDATE History SET Notes='tamper'")); Assert.ThrowsAny<Exception>(() => db.Database.ExecuteSqlRaw("DELETE FROM History"));
    }
    [Theory] [InlineData("Adarsh")] [InlineData("darsh sal")] [InlineData("8296419486")] [InlineData("SONU")] [InlineData("96")]
    public void SearchFindsPlayer(string query) => Assert.True(PlayerRules.Matches(Player(), query));
    [Fact] public void CalculationsUseSoldPlayersOnly()
    {
        var players = new[] { new Player { Status = AuctionStatus.Sold, SoldPaise = 250000 }, new Player { Status = AuctionStatus.Sold, SoldPaise = 750000 }, new Player { Status = AuctionStatus.Unsold }, new Player() };
        var stats = Statistics.Calculate(players); Assert.Equal(4, stats.Total); Assert.Equal(1000000, stats.TotalPaise); Assert.Equal(500000, stats.AveragePaise); Assert.Equal(750000, stats.HighestPaise); Assert.Equal(1, stats.Unsold); Assert.Equal(1, stats.Available);
    }
    private string Excel(params string[][] rows)
    {
        var path = Path.Combine(root, Guid.NewGuid() + ".xlsx"); using var book = new XLWorkbook(); var sheet = book.AddWorksheet("Registrations");
        for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) sheet.Cell(r + 1, c + 1).Value = rows[r][c]; book.SaveAs(path); return path;
    }
    [Fact] public void ExcelAliasesValidationAndDuplicateDecisions()
    {
        var importer = new ImportService(workspace, log);
        var file = Excel(["Player Name", "Mobile", "Age", "Jersey No", "City"], ["Adarsh Saliyan", "8296419486", "24", "96", "Aroor"], ["Broken", "abc", "twenty", "x", ""], ["Adarsh Saliyan", "8296419486", "24", "96", "Belmar"]);
        var rows = importer.Preview(file); Assert.Equal("Valid", rows[0].Result); Assert.NotEmpty(rows[1].Error); Assert.True(rows[2].IsDuplicate);
        Assert.Equal(1, importer.Import(rows)); Assert.Single(service.Players()); Assert.Contains("Aroor", service.Players()[0].OriginalRegistrationJson);
        var second = importer.Preview(Excel(["Name", "Phone Number", "Age"], ["Adarsh Saliyan", "8296419486", "25"])); Assert.True(second[0].IsDuplicate); second[0].Resolution = DuplicateResolution.UpdateExisting;
        importer.Import(second); Assert.Equal(25, service.Players()[0].Age); Assert.Contains("Aroor", service.Players()[0].OriginalRegistrationJson);
        second = importer.Preview(Excel(["Name", "Phone"], ["Another", "8296419486"])); second[0].Resolution = DuplicateResolution.ImportAsNew; importer.Import(second); Assert.Equal(2, service.Players().Count);
    }
    [Fact] public void SharedPhoneRegistrationsImportDistinctNamesAndReimportWithoutDuplicates()
    {
        var importer = new ImportService(workspace, log);
        var file = Excel(["Name", "Phone", "Jersey No"], ["Vijaya", "9000000001", "1"], ["Krishna", "9000000001", "18"], ["Vinaya", "9000000001", "2"]);
        var rows = importer.Preview(file);
        Assert.All(rows, row => Assert.False(row.IsDuplicate));
        Assert.Equal(3, importer.Import(rows));
        Assert.Equal(0, importer.Import(importer.Preview(file)));
        var update = importer.Preview(Excel(["Name", "Phone", "Jersey No"], ["Krishna", "9000000001", "19"]));
        update[0].Resolution = DuplicateResolution.UpdateExisting;
        Assert.Equal(1, importer.Import(update));
        Assert.Equal("19", service.Players().Single(p => p.FullName == "Krishna").JerseyNumber);
        Assert.Equal("1", service.Players().Single(p => p.FullName == "Vijaya").JerseyNumber);
    }
    [Fact] public void RegistrationTyposAreCorrectedWithoutLosingOriginalValues()
    {
        var importer = new ImportService(workspace, log);
        var file = Excel(["Name", "Phone", "Jersey Name", "Jersey No"],
            ["Athul", "9000000001", "22", "Athul"],
            ["Santhosh", "9000000002", "Santhu", "O3"],
            ["Sujith", "9000000003", "Sujith", "O7"],
            ["Sandeep", "9000000004", "Sandeep", "O5"],
            ["Mahendra", "9000000005", "Bahubali", "O7"]);
        var rows = importer.Preview(file);
        Assert.All(rows, row => { Assert.Empty(row.Error); Assert.NotEmpty(row.Warning); });
        Assert.Equal(new[] { "22", "03", "07", "05", "07" }, rows.Select(r => r.Player.JerseyNumber));
        Assert.Equal("Athul", rows[0].Player.JerseyName);
        Assert.Contains("O3", rows[1].Player.OriginalRegistrationJson);
        Assert.Equal(5, importer.Import(rows));
        Assert.Equal(0, importer.Import(importer.Preview(file)));
    }
    [Fact] public void SameNameAndJerseyWithDifferentPhonesAreDistinctPlayers()
    {
        var importer = new ImportService(workspace, log);
        var file = Excel(["Name", "Phone", "Jersey No"], ["Deekshith", "9000000001", "8"], ["Deekshith", "9000000002", "8"]);
        var rows = importer.Preview(file);
        Assert.All(rows, row => Assert.False(row.IsDuplicate));
        Assert.Equal(2, importer.Import(rows));
    }
    [Fact] public void SameNameAndPhoneWithCountryCodeRequiresExplicitDuplicateDecision()
    {
        var importer = new ImportService(workspace, log);
        var file = Excel(["Name", "Phone", "Jersey No"], ["Ajith", "+91 78995 77238", "02"], ["AJITH", "7899577238", "02"]);
        var rows = importer.Preview(file);
        Assert.True(rows[1].IsDuplicate);
        Assert.Equal(1, importer.Import(rows));
        var retry = importer.Preview(file);
        retry[1].Resolution = DuplicateResolution.ImportAsNew;
        Assert.Equal(1, importer.Import(retry));
        Assert.Equal(2, service.Players().Count);
    }
    [Fact] public void AmbiguousJerseyTextRemainsInvalid()
    {
        var rows = new ImportService(workspace, log).Preview(Excel(["Name", "Phone", "Jersey Name", "Jersey No"], ["Athul", "9000000001", "22", "Someone Else"]));
        Assert.NotEmpty(rows[0].Error);
        Assert.Empty(rows[0].Warning);
    }
    [Fact] public void MissingColumnsAreReported() => Assert.Throws<InvalidOperationException>(() => new ImportService(workspace, log).Preview(Excel(["Unknown"], ["Adarsh"])));
    [Fact] public void CsvSupportsQuotedCommasAndDuplicates()
    {
        var path = Path.Combine(root, "players.csv"); File.WriteAllText(path, "Full Name,Phone Number,Area / City\n\"Saliyan, Adarsh\",8296419486,\"Aroor, Karnataka\"\n");
        var import = new ImportService(workspace, log); var rows = import.Preview(path); Assert.Equal("Saliyan, Adarsh", rows[0].Player.FullName); import.Import(rows); Assert.True(import.Preview(path)[0].IsDuplicate);
    }
    [Theory] [InlineData("-1")] [InlineData("1.001")] [InlineData("abc")]
    public void InvalidMoneyRejected(string input) => Assert.Throws<InvalidOperationException>(() => Money.Parse(input));
    [Fact] public void CurrencyUsesIndianGrouping() { Assert.Equal(250000, Money.Parse("₹2,500")); Assert.Equal("₹1,00,000", Money.Format(10000000)); }
    [Theory] [InlineData("https://drive.google.com/open?id=abc_123")] [InlineData("https://drive.google.com/file/d/abc_123/view")] [InlineData("https://drive.google.com/uc?id=abc_123")]
    public void DriveLinksNormalize(string url) => Assert.Equal("https://drive.google.com/uc?export=download&id=abc_123", ImageService.NormalizeUrl(url));
    [Theory]
    [InlineData("47494638376100000000")]
    [InlineData("47494638396100000000")]
    [InlineData("424d0000000000000000")]
    [InlineData("49492a00000000000000")]
    [InlineData("4d4d002a000000000000")]
    [InlineData("00000100000000000000")]
    [InlineData("524946460000000057454250")]
    public void CommonPhotoFormatsAreRecognized(string signature) => Assert.True(ImageService.IsImage(Convert.FromHexString(signature)));
    [Fact] public async Task InvalidPhotoIsSafe()
    {
        var images = new ImageService(log);
        Assert.Null(await images.FetchAsync("not a URL")); Assert.False(ImageService.IsImage("<html>login</html>"u8.ToArray()));
        Assert.Contains("HTTPS", (await images.FetchWithResultAsync("not a URL")).Error);
        Assert.True(ImageService.IsAvif([0, 0, 0, 0, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66]));
    }
    [Fact] public void ExcelReportCanReopenAndContainsHistory()
    {
        var p = Player(); var t = Team(); service.Sell(p.Id, t.Id, 250000, ""); var file = Path.Combine(root, "report.xlsx"); new ExportService(workspace, service, log).Export(file, service.Players(), true);
        using var book = new XLWorkbook(file); Assert.Equal(5, book.Worksheets.Count); Assert.Equal("Adarsh Saliyan", book.Worksheet("Players").Cell(2, 2).GetString()); Assert.Equal(2500, book.Worksheet("Players").Cell(2, 14).GetDouble()); Assert.Equal("Sold", book.Worksheet("Auction History").Cell(2, 5).GetString());
        var purchases = book.Worksheet("Team Purchases"); Assert.Equal("Adarsh Saliyan", purchases.Cell(2, 3).GetString()); Assert.Equal(2500, purchases.Cell(2, 10).GetDouble()); Assert.Equal("TEAM TOTAL", purchases.Cell(3, 2).GetString()); Assert.Equal("AUCTION TOTAL", purchases.Cell(4, 2).GetString()); Assert.Equal(2500, purchases.Cell(4, 10).GetDouble());
    }
    [Fact] public void TeamPdfContainsOnePagePerTeam()
    {
        var player = Player(); var team = Team(); service.Sell(player.Id, team.Id, 250000, "");
        var photo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWZkAAAAASUVORK5CYII="); service.SavePhoto(player.Id, photo, true);
        var file = Path.Combine(root, "team-report.pdf"); new TeamReportPdfService(log).Generate(file, service.Teams(), service.Players(), "VPL 2026");
        using var document = PdfReader.Open(file, PdfDocumentOpenMode.Import); Assert.Single(document.Pages); Assert.True(new FileInfo(file).Length > 1000);
    }
    [Fact] public void PowerPointIsValidWithOneSlidePerPlayerAndEmbeddedPhoto()
    {
        var p = Player(); service.MarkUnsold(p.Id); var q = Player("Nithesh", "7899984478", "89"); service.MarkUnsold(q.Id);
        var photo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWZkAAAAASUVORK5CYII="); service.SavePhoto(p.Id, photo, true);
        var file = Path.Combine(root, "unsold.pptx"); new PowerPointService(log).Generate(file, service.Players(), "VPL 2026", 1);
        using var document = PresentationDocument.Open(file, false); Assert.Empty(new OpenXmlValidator().Validate(document)); Assert.Equal(2, document.PresentationPart!.SlideParts.Count()); Assert.Single(document.PresentationPart.SlideParts.SelectMany(s => s.ImageParts));
        Assert.Contains("Adarsh Saliyan", string.Join("", document.PresentationPart.SlideParts.Select(s => s.Slide.InnerText)));
    }
    [Fact] public void PowerPointOmitsJerseyAndMarksMissingBowlingAsNA()
    {
        var player = new Player { FullName = "Test Player", PlayerType = "Batsman", BowlingStyle = "", JerseyName = "TEST", JerseyNumber = "3", JerseySize = "L" };
        var file = Path.Combine(root, "profile.pptx"); new PowerPointService(log).Generate(file, [player], "VPL 2026", 1);
        using var document = PresentationDocument.Open(file, false); var text = string.Join("", document.PresentationPart!.SlideParts.Select(s => s.Slide.InnerText));
        Assert.Contains("BOWLING", text); Assert.Contains("NA", text); Assert.DoesNotContain("Jersey", text);
    }
    [Fact] public void LongCityPowerPointUsesExplicitLinesWithinItsBox()
    {
        var player = new Player { FullName = "Adarsh Shetty", AreaCity = "Neelavara Cross, Arooru village" };
        var file = Path.Combine(root, "long-city.pptx");
        new PowerPointService(log).Generate(file, [player], "Vishnumurti Premier League", 1);
        using var document = PresentationDocument.Open(file, false);
        Assert.Empty(new OpenXmlValidator().Validate(document));
        var shape = document.PresentationPart!.SlideParts.Single().Slide.Descendants<DocumentFormat.OpenXml.Presentation.Shape>()
            .Single(s => s.NonVisualShapeProperties!.NonVisualDrawingProperties!.Name == "City");
        var paragraphs = shape.TextBody!.Elements<DocumentFormat.OpenXml.Drawing.Paragraph>().ToList();
        Assert.Equal(player.AreaCity, string.Join(" ", paragraphs.Select(p => p.InnerText)));
        Assert.True(paragraphs.Count > 1);
        var linePoints = paragraphs.Sum(p => p.ParagraphProperties!.LineSpacing!.GetFirstChild<DocumentFormat.OpenXml.Drawing.SpacingPoints>()!.Val!.Value / 100d);
        Assert.True(linePoints <= shape.ShapeProperties!.Transform2D!.Extents!.Cy!.Value / 12700d);
        Assert.NotNull(shape.TextBody.BodyProperties!.GetFirstChild<DocumentFormat.OpenXml.Drawing.NoAutoFit>());
    }
    [Fact] public void BackupRestoreIncludesPhotosAndSurvivesReopen()
    {
        var p = Player(); var t = Team(); service.Sell(p.Id, t.Id, 250000, ""); service.SavePhoto(p.Id, [1, 2, 3], true);
        var backup = new BackupService(workspace, log); var file = Path.Combine(root, "backup.db"); backup.Backup(file);
        Assert.False(File.Exists(file + "-wal")); Assert.False(File.Exists(file + "-shm"));
        Assert.Empty(Directory.GetFiles(root, "*.tmp*"));
        service.Undo(); backup.Restore(file, Path.Combine(root, "restored.db")); var reopened = new AuctionService(new Workspace(root), log);
        Assert.Equal(AuctionStatus.Sold, reopened.Players()[0].Status); Assert.Equal(new byte[] { 1, 2, 3 }, reopened.Players()[0].Photo); Assert.Single(reopened.History()); Assert.Equal(250000, reopened.Teams()[0].SpentPaise);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
