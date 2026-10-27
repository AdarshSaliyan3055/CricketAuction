using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CricketAuction.App.ViewModels;
using CricketAuction.Domain;
using CricketAuction.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ClosedXML.Excel;

namespace CricketAuction.App;
// Opt-in runtime verification using an explicitly supplied isolated data directory.
internal static class SmokeHarness
{
    public static void Prepare(ServiceProvider services)
    {
        if (!Environment.GetCommandLineArgs().Contains("--data-dir")) throw new InvalidOperationException("Smoke testing requires an isolated --data-dir.");
        var workspace = services.GetRequiredService<Workspace>(); var auction = services.GetRequiredService<AuctionService>();
        // Every verification run creates a separate auction, preserving earlier test artifacts.
        {
            File.Delete(Path.Combine(workspace.Root, "smoke-error.txt"));
            File.Delete(Path.Combine(workspace.Root, "smoke-ok.txt"));
            workspace.Create("Vishnumurthi Premier League", "VPL 2026 Auction");
            auction.SaveTeam(new Team { Name = "Team A", OwnerName = "Captain A", BudgetPaise = 2000000 });
            auction.SaveTeam(new Team { Name = "Team B", OwnerName = "Captain B", BudgetPaise = 2000000 });
            var input = Path.Combine(workspace.Root, "registrations.xlsx");
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("Registrations");
                string[][] rows = [["Full Name", "Age", "Phone Number", "Player Type", "Batting Style", "Bowling Style", "Area / City", "Jersey Name", "Jersey Number", "Jersey Size"],
                    ["Adarsh Saliyan", "24", "8296419486", "Batsman", "Right Hand", "Don't Bowl", "Aroor", "SONU", "96", "XL"],
                    ["Nithesh Poojari", "36", "7899984478", "All-rounder", "Right Hand", "Right Arm Medium", "Belmar", "NITHESH", "89", "L"]];
                for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) sheet.Cell(r + 1, c + 1).Value = rows[r][c];
                book.SaveAs(input);
            }
            var importer = services.GetRequiredService<IImportService>();
            if (importer.Import(importer.Preview(input)) != 2) throw new InvalidOperationException("Acceptance import failed.");
        }
        var vm = services.GetRequiredService<MainViewModel>(); vm.Refresh(); vm.Selected = vm.Players.FirstOrDefault(); vm.Notice = "Auction ready · Sample data in an isolated verification database";
    }
    public static void Capture(Window window, ServiceProvider services)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(services.GetRequiredService<Workspace>().Root, "dashboard.png")); encoder.Save(output);
        var vm = services.GetRequiredService<MainViewModel>();
        var auction = services.GetRequiredService<AuctionService>();
        var dialogs = services.GetRequiredService<DialogService>();
        CheckDialog("sale", () => dialogs.Sale(vm.Selected!, auction.Teams()), services);
        CheckDialog("player-edit", () => dialogs.EditPlayer(vm.Selected!), services);
        CheckDialog("teams", () => dialogs.Teams(auction, vm.Session!), services);
        CheckDialog("settings", () => dialogs.Settings(vm.Session!), services);
        var workspace = services.GetRequiredService<Workspace>();
        CheckDialog("auction-team-names", () => dialogs.EditAuctionNames(workspace, workspace.Auctions().Single(a => a.DatabasePath == workspace.DatabasePath)), services);
        CheckDialog("import-preview", () => dialogs.ImportPreview([new ImportRow { RowNumber = 2, Player = vm.Selected! }]), services);
        VerifyWorkflow(services);
        File.WriteAllText(Path.Combine(services.GetRequiredService<Workspace>().Root, "smoke-ok.txt"), "PASS: WPF dashboard/profile and sale, edit, teams, settings and import dialogs rendered. Excel import, search, sale, budget/statistics refresh, unsold PPT, round two sale, history, complete Excel export, team PDF/PNG poster generation, backup, restore and database reopen verified.");
    }
    private static void VerifyWorkflow(ServiceProvider services)
    {
        var workspace = services.GetRequiredService<Workspace>(); var auction = services.GetRequiredService<AuctionService>(); var vm = services.GetRequiredService<MainViewModel>();
        var players = auction.Players(); var adarsh = players.Single(p => p.FullName == "Adarsh Saliyan"); var nithesh = players.Single(p => p.FullName == "Nithesh Poojari");
        // Exercise the shipped native codec, database storage, and WPF decoder together.
        using (var phonePhoto = new ImageMagick.MagickImage(ImageMagick.MagickColors.Coral, 2400, 1200))
        {
            var normalized = services.GetRequiredService<DialogService>().NormalizePhoto(phonePhoto.ToByteArray(ImageMagick.MagickFormat.Avif));
            auction.SavePhoto(adarsh.Id, normalized, true);
            var stored = auction.Players().Single(p => p.Id == adarsh.Id).Photo!;
            var display = new PhotoConverter().Convert(stored, typeof(System.Windows.Media.ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture);
            if (display is not BitmapSource { PixelWidth: 1600, PixelHeight: 800 }) throw new InvalidOperationException("Converted phone photo did not display correctly.");
        }
        var teams = auction.Teams();
        vm.Selected = vm.Players.Single(p => p.Id == adarsh.Id);
        if (!vm.CanAuctionSelected || !vm.SoldCommand.CanExecute(null) || !vm.UnsoldCommand.CanExecute(null)) throw new InvalidOperationException("Available player actions must be enabled.");
        if (adarsh.Status == AuctionStatus.Available) auction.Sell(adarsh.Id, teams[0].Id, Money.Parse("₹2,500"), "Acceptance sale");
        vm.Refresh(); vm.Search = "Adarsh";
        vm.Selected = vm.Players.Single(p => p.Id == adarsh.Id);
        if (vm.CanAuctionSelected || vm.SoldCommand.CanExecute(null) || vm.UnsoldCommand.CanExecute(null)) throw new InvalidOperationException("Sold player actions must be disabled.");
        if (vm.Players.Count != 1 || vm.Players[0].SoldPaise != 250000) throw new InvalidOperationException("Sale/search refresh failed.");
        vm.Search = "";
        if (nithesh.AuctionRound == 1 && nithesh.Status == AuctionStatus.Available) auction.MarkUnsold(nithesh.Id);
        vm.Refresh(); vm.Selected = auction.Players().Single(p => p.Id == nithesh.Id);
        if (vm.CanAuctionSelected || vm.SoldCommand.CanExecute(null) || vm.UnsoldCommand.CanExecute(null)) throw new InvalidOperationException("Unsold player actions must be disabled.");
        if (workspace.Session().CurrentRound == 1)
        {
            vm.Refresh(); vm.Section = "Unsold";
            if (vm.Players.Count != 1) throw new InvalidOperationException("Unsold list did not update.");
            services.GetRequiredService<IPowerPointService>().Generate(Path.Combine(workspace.Root, "Unsold_Players_Round_1.pptx"), vm.Players.ToList(), vm.Title, 1);
            auction.StartNextRound(); vm.Refresh(); vm.Selected = auction.Players().Single(p => p.Id == nithesh.Id);
            if (!vm.CanAuctionSelected || !vm.SoldCommand.CanExecute(null) || !vm.UnsoldCommand.CanExecute(null)) throw new InvalidOperationException("Next-round player actions must be enabled.");
            auction.Sell(nithesh.Id, teams[1].Id, 300000, "Acceptance round two sale");
        }
        vm.Section = "Dashboard"; vm.Refresh();
        if (vm.Stats.Sold != 2 || vm.Stats.TotalPaise != 550000 || auction.History(nithesh.Id).Count != 3) throw new InvalidOperationException("Round two totals/history failed.");
        services.GetRequiredService<IExportService>().Export(Path.Combine(workspace.Root, "Complete_Auction_Report.xlsx"), auction.Players(), true);
        var soldPlayers = auction.Players().Where(player => player.Status == AuctionStatus.Sold).ToList();
        VerifyPresentationOrder(services, soldPlayers);
        var purchasingTeams = auction.Teams().Where(team => soldPlayers.Any(player => player.TeamId == team.Id)).ToList();
        var longTextPlayers = Enumerable.Range(1, 9).Select(index => new Player
        {
            FullName = index == 1 ? "Adarsh Shetty" : "A player with a particularly long display name",
            AreaCity = "Neelavara Cross, Arooru village", PlayerType = "All-rounder",
            BattingStyle = "Right Hand", BowlingStyle = "Right Arm Medium Fast",
            Status = AuctionStatus.Sold, TeamId = purchasingTeams[0].Id, SoldPaise = 123456789,
            Photo = soldPlayers[0].Photo
        }).ToList();
        services.GetRequiredService<TeamReportPdfService>().Generate(Path.Combine(workspace.Root, "Long_Text_Report.pdf"), purchasingTeams.Take(1).ToList(), longTextPlayers, "Vishnumurti Premier League Coastal Cricket Championship");
        services.GetRequiredService<IPowerPointService>().Generate(Path.Combine(workspace.Root, "Long_Text_Players.pptx"), longTextPlayers, "Vishnumurti Premier League Coastal Cricket Championship", 1);
        services.GetRequiredService<TeamReportPdfService>().Generate(Path.Combine(workspace.Root, "Team_Report.pdf"), purchasingTeams, soldPlayers, workspace.Session().TournamentName);
        services.GetRequiredService<TeamPosterImageService>().Generate(Path.Combine(workspace.Root, "Team_Poster.png"), purchasingTeams[0], soldPlayers, workspace.Session().TournamentName);
        var posterRenderer = services.GetRequiredService<TeamPosterImageService>();
        var posterTeam = purchasingTeams[0];
        var posterPlayers = Enumerable.Range(1, 17).Select(index => new Player
        {
            FullName = index == 1 ? "A player with a particularly long display name" : $"Squad Player {index}",
            PlayerType = index % 2 == 0 ? "All-rounder" : "Batter", TeamId = posterTeam.Id,
            Status = AuctionStatus.Sold, Photo = index == 2 ? new byte[12] : null
        }).ToList();
        foreach (var design in Enum.GetValues<TeamPosterDesign>())
        {
            var posterPath = Path.Combine(workspace.Root, $"Poster_{design}.png");
            posterRenderer.Generate(posterPath, posterTeam, posterPlayers, "Coastal Cricket Championship", design, "Season 2027 Auction");
            using var posterStream = File.OpenRead(posterPath);
            var frame = BitmapFrame.Create(posterStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (frame.PixelWidth != 1600 || frame.PixelHeight < 1000) throw new InvalidOperationException("Poster export dimensions failed.");
        }
        CheckDialog("poster-designs", () => services.GetRequiredService<DialogService>().ChoosePosterDesign(posterRenderer, posterTeam, posterPlayers, workspace.Session()), services);
        var backup = services.GetRequiredService<IBackupService>(); var backupPath = Path.Combine(workspace.Root, "Verified_Backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db"); backup.Backup(backupPath);
        backup.Restore(backupPath, Path.Combine(workspace.Root, "Restored_" + Guid.NewGuid().ToString("N") + ".db"));
        var reopened = new Workspace(workspace.Root); var verified = new AuctionService(reopened, services.GetRequiredService<AppLog>());
        if (Statistics.Calculate(verified.Players()).TotalPaise != 550000) throw new InvalidOperationException("Persistence/restore failed.");
        vm.Refresh(); vm.Selected = vm.Players.FirstOrDefault(p => p.Id == nithesh.Id);
    }
    private static void VerifyPresentationOrder(ServiceProvider services, IReadOnlyList<Player> players)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var dialog = Application.Current.Windows.Cast<Window>().Single(w => w.Title == "Arrange PowerPoint players");
            IEnumerable<DependencyObject> Descendants(DependencyObject parent)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                    foreach (var descendant in Descendants(child)) yield return descendant;
                }
            }
            var controls = Descendants(dialog).ToList();
            var last = controls.OfType<System.Windows.Controls.Button>().Single(b => Equals(b.Content, "Move to last"));
            last.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            dialog.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(dialog), 0, System.Windows.Input.Key.Enter)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        };
        timer.Start();
        var ordered = services.GetRequiredService<DialogService>().ArrangePresentationPlayers(players, "All sold players");
        timer.Stop();
        var expected = players.Skip(1).Append(players[0]).ToList();
        if (ordered == null || !ordered.SequenceEqual(expected)) throw new InvalidOperationException("Presentation reorder/Enter confirmation failed.");
        var path = Path.Combine(services.GetRequiredService<Workspace>().Root, "Ordered_Players.pptx");
        services.GetRequiredService<IPowerPointService>().Generate(path, ordered, "Ordering verification", 1);
        using var document = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, false);
        var presentation = document.PresentationPart!;
        var slides = presentation.Presentation.SlideIdList!.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().ToList();
        if (slides.Count != expected.Count) throw new InvalidOperationException("Ordered presentation slide count failed.");
        for (var i = 0; i < slides.Count; i++)
        {
            var slide = (DocumentFormat.OpenXml.Packaging.SlidePart)presentation.GetPartById(slides[i].RelationshipId!);
            if (!slide.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Any(text => text.Text == expected[i].FullName))
                throw new InvalidOperationException("Presentation did not preserve the chosen player order.");
        }
        CheckDialog("presentation-order", () => services.GetRequiredService<DialogService>().ArrangePresentationPlayers(players, "All sold players"), services);
    }

    private static void CheckDialog(string name, Action show, ServiceProvider services)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w != Application.Current.MainWindow && w.IsVisible);
            if (dialog == null) throw new InvalidOperationException("Dialog did not open: " + name);
            dialog.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(dialog);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(services.GetRequiredService<Workspace>().Root, name + ".png")); encoder.Save(output); dialog.Close();
        };
        timer.Start(); show(); timer.Stop();
    }
}
