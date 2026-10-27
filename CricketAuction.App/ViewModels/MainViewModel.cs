using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CricketAuction.Domain;
using CricketAuction.Infrastructure;

namespace CricketAuction.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly Workspace workspace;
    private readonly AuctionService auction;
    private readonly IImportService importer;
    private readonly IExportService exporter;
    private readonly IPowerPointService ppt;
    private readonly IImageService images;
    private readonly IBackupService backup;
    private readonly TeamReportPdfService teamReportPdf;
    private readonly TeamPosterImageService teamPosterImage;
    private readonly DialogService dialogs;
    private readonly AppLog log;
    private List<Player> all = [];
    private Player? selected;
    private string search = "", section = "Dashboard", type = "All", batting = "All", bowling = "All", city = "All", round = "All", team = "All";
    private string notice = "Create or open an auction to begin.", photoProgressText = "Not downloaded yet.";
    private bool busy, isCachingPhotos, canRestorePhoto;
    private CancellationTokenSource? photoCancellation;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
    public AuctionSession? Session { get; private set; }
    public bool HasSession => workspace.DatabasePath.Length > 0;
    public string Title => Session?.TournamentName ?? "Cricket Auction";
    public string Subtitle => Session == null ? "Create or open an auction" : $"{Session.AuctionName} • Round {Session.CurrentRound}";
    public Statistics Stats { get; private set; } = Statistics.Calculate([]);
    public string Notice { get => notice; set => Set(ref notice, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsCachingPhotos { get => isCachingPhotos; private set { Set(ref isCachingPhotos, value); Changed(nameof(PhotoReadiness)); CommandManager.InvalidateRequerySuggested(); } }
    public string PhotoProgressText { get => photoProgressText; private set { Set(ref photoProgressText, value); Changed(nameof(PhotoReadiness)); } }
    public ObservableCollection<PhotoFailure> PhotoFailures { get; } = [];
    public bool HasPhotoFailures => PhotoFailures.Count > 0;
    public ICommand NewCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand AuctionsCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand PptCommand { get; }
    public ICommand TeamReportPdfCommand { get; }
    public ICommand TeamPosterImageCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand TeamsCommand { get; }
    public ICommand SoldCommand { get; }
    public ICommand UnsoldCommand { get; }
    public ICommand RoundCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand PhotoCommand { get; }
    public ICommand RotatePhotoCommand { get; }
    public ICommand RestorePhotoCommand { get; }
    public ICommand CacheCommand { get; }
    public ICommand CancelPhotosCommand { get; }
    public ICommand PhotoFailuresCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RandomCommand { get; }
    public ICommand ProjectorCommand { get; }

    public MainViewModel(Workspace workspace, AuctionService auction, IImportService importer, IExportService exporter,
        IPowerPointService ppt, IImageService images, IBackupService backup, TeamReportPdfService teamReportPdf,
        TeamPosterImageService teamPosterImage, DialogService dialogs, AppLog log)
    {
        this.workspace = workspace; this.auction = auction; this.importer = importer; this.exporter = exporter;
        this.ppt = ppt; this.images = images; this.backup = backup; this.teamReportPdf = teamReportPdf;
        this.teamPosterImage = teamPosterImage; this.dialogs = dialogs; this.log = log;
        ICommand Command(Func<Task> action, Func<bool>? can = null, bool session = true) =>
            new DelegateCommand(async () => await Guard(action), () => !Busy && (!session || HasSession) && (can?.Invoke() ?? true));
        NewCommand = Command(New, session: false); OpenCommand = Command(Open, session: false);
        AuctionsCommand = Command(OpenSavedAuction, session: false); RestoreCommand = Command(Restore, session: false);
        ImportCommand = Command(Import); ExportCommand = Command(Export); PptCommand = Command(PowerPoint);
        TeamReportPdfCommand = Command(TeamReportPdf); TeamPosterImageCommand = Command(TeamPosterImage);
        BackupCommand = Command(Backup); SettingsCommand = Command(Settings); TeamsCommand = Command(TeamsDialog);
        SoldCommand = Command(Sell, () => CanAuctionSelected); UnsoldCommand = Command(Unsold, () => CanAuctionSelected);
        RoundCommand = Command(NextRound); UndoCommand = Command(Undo);
        EditCommand = Command(Edit, () => Selected != null); AddCommand = Command(Add);
        PhotoCommand = Command(ChangePhoto, () => Selected != null);
        RotatePhotoCommand = Command(RotatePhoto, () => Selected?.Photo is { Length: > 0 });
        RestorePhotoCommand = Command(RestorePhoto, () => CanRestorePhoto);
        CacheCommand = Command(CachePhotos);
        CancelPhotosCommand = new DelegateCommand(() => { photoCancellation?.Cancel(); PhotoProgressText = "Cancelling download; keeping saved photos…"; CommandManager.InvalidateRequerySuggested(); },
            () => IsCachingPhotos && photoCancellation is { IsCancellationRequested: false });
        PhotoFailuresCommand = Command(() => { dialogs.PhotoFailures(PhotoFailures.ToList()); return Task.CompletedTask; }, () => HasPhotoFailures);
        RefreshCommand = Command(() => { Refresh(); return Task.CompletedTask; });
        RandomCommand = Command(() =>
        {
            if (!CheckReadiness()) return Task.CompletedTask;
            Search = ""; Section = "Available"; TypeFilter = BattingFilter = BowlingFilter = CityFilter = RoundFilter = TeamFilter = "All";
            Selected = Players.Count == 0 ? null : Players[Random.Shared.Next(Players.Count)];
            Notice = Selected == null ? "No available players in this round." : $"Next player: {Selected.FullName}";
            return Task.CompletedTask;
        });
        ProjectorCommand = Command(() => { dialogs.Projector(this); return Task.CompletedTask; }, () => Selected != null);
    }
    private async Task Guard(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        try { await action(); }
        catch (Exception ex) { log.Write("Auction operation failed", ex); Notice = ex.Message; dialogs.Error(ex.Message); }
        finally { Busy = false; }
    }
    private bool CheckReadiness()
    {
        if (Teams.Count == 0 || all.Count == 0)
        {
            dialogs.Error($"Auction is not ready.\n{TeamReadiness}\n{PlayerReadiness}\nConfigure teams and import players before bidding."); return false;
        }
        return Stats.Sold > 0 || dialogs.Confirm($"Auction readiness\n\n{TeamReadiness}\n{PlayerReadiness}\n{PhotoReadiness}\n\nPhotos are optional. Continue to bidding?");
    }
    public string TotalValue => Money.Format(Stats.TotalPaise);
    public string AverageValue => Money.Format(Stats.AveragePaise);
    public string HighestValue => Money.Format(Stats.HighestPaise);
    public string TotalPurse => Teams.Any(team => !team.BudgetPaise.HasValue) ? "Unlimited" : Money.Format(Teams.Sum(team => team.BudgetPaise!.Value));
    public string TeamReadiness => Teams.Count == 0 ? "No teams configured" : $"{Teams.Count} teams configured";
    public string PlayerReadiness => all.Count == 0 ? "No players imported" : $"{all.Count} players imported";
    public string PhotoReadiness
    {
        get
        {
            if (IsCachingPhotos) return PhotoProgressText;
            var linked = all.Count(player => !string.IsNullOrWhiteSpace(player.PlayerPhotoUrl));
            var cached = all.Count(player => !string.IsNullOrWhiteSpace(player.PlayerPhotoUrl) && player.Photo is { Length: > 0 });
            return $"{all.Count} players in auction · Photos: {cached}/{linked} linked photos saved · {all.Count - linked} players without photo links"
                + (HasPhotoFailures ? $"; {PhotoFailures.Count} failed on last attempt" : "");
        }
    }
    public string LastSale => all.Where(p => p.Status == AuctionStatus.Sold).OrderByDescending(p => p.SoldAt).FirstOrDefault() is { } p ? $"{p.FullName}  •  {p.TeamName}  •  {p.SoldPrice}" : "No sales yet";
    public string Leaders => string.Join("   •   ", all.Where(p => p.Status == AuctionStatus.Sold).OrderByDescending(p => p.SoldPaise).Take(3).Select(p => $"{p.FullName} {p.SoldPrice}"));
    public ObservableCollection<Player> Players { get; } = [];
    public ObservableCollection<Team> Teams { get; } = [];
    public ObservableCollection<AuctionHistory> History { get; } = [];
    public bool HasPlayers => Players.Count > 0;
    public List<string> Types { get; private set; } = ["All"];
    public List<string> BattingStyles { get; private set; } = ["All"];
    public List<string> BowlingStyles { get; private set; } = ["All"];
    public List<string> Cities { get; private set; } = ["All"];
    public List<string> Rounds { get; private set; } = ["All"];
    public List<string> TeamNames { get; private set; } = ["All"];
    public string Search { get => search; set { if (Set(ref search, value)) Filter(); } }
    public string Section { get => section; set { if (Set(ref section, value)) { Filter(); Changed(nameof(ListTitle)); } } }
    public string TypeFilter { get => type; set { if (Set(ref type, value ?? "All")) Filter(); } }
    public string BattingFilter { get => batting; set { if (Set(ref batting, value ?? "All")) Filter(); } }
    public string BowlingFilter { get => bowling; set { if (Set(ref bowling, value ?? "All")) Filter(); } }
    public string CityFilter { get => city; set { if (Set(ref city, value ?? "All")) Filter(); } }
    public string RoundFilter { get => round; set { if (Set(ref round, value ?? "All")) Filter(); } }
    public string TeamFilter { get => team; set { if (Set(ref team, value ?? "All")) Filter(); } }
    public string ListTitle => $"{(Section == "Dashboard" ? "Registered" : Section).ToUpperInvariant()}  /  {Players.Count} players";
    public Player? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return; History.Clear(); CanRestorePhoto = value != null && HasSession && auction.HasOriginalPhoto(value.Id);
            if (value != null && HasSession) foreach (var h in auction.History(value.Id)) History.Add(h);
            Changed(nameof(ProfileVisible)); Changed(nameof(PhotoMessage)); Changed(nameof(CanAuctionSelected)); CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool CanRestorePhoto { get => canRestorePhoto; private set { if (Set(ref canRestorePhoto, value)) Changed(nameof(CanRestorePhoto)); } }
    public bool ProfileVisible => Selected != null;
    public bool CanAuctionSelected => Session != null && Selected is { Status: AuctionStatus.Available } player && player.AuctionRound == Session.CurrentRound;
    public string PhotoMessage => Selected?.Photo == null ? "PLAYER\nPHOTO\n\nPhoto unavailable\nChoose a local photo or cache photos" : "";
    private static List<string> Options(IEnumerable<string> values) => ["All", .. values.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Order()];
    public void Refresh()
    {
        if (!HasSession) return; var id = Selected?.Id; Session = workspace.Session(); all = auction.Players(); Stats = Statistics.Calculate(all);
        Teams.Clear(); foreach (var t in auction.Teams()) Teams.Add(t);
        Types = Options(all.Select(p => p.PlayerType)); BattingStyles = Options(all.Select(p => p.BattingStyle)); BowlingStyles = Options(all.Select(p => p.BowlingStyle)); Cities = Options(all.Select(p => p.AreaCity));
        Rounds = Options(all.Select(p => p.AuctionRound.ToString())); TeamNames = Options(Teams.Select(t => t.Name));
        foreach (var property in new[] { nameof(Session), nameof(HasSession), nameof(Title), nameof(Subtitle), nameof(Stats), nameof(TotalValue), nameof(AverageValue), nameof(HighestValue), nameof(TotalPurse), nameof(TeamReadiness), nameof(PlayerReadiness), nameof(PhotoReadiness), nameof(LastSale), nameof(Leaders), nameof(Types), nameof(BattingStyles), nameof(BowlingStyles), nameof(Cities), nameof(Rounds), nameof(TeamNames) }) Changed(property);
        Filter(); Selected = all.FirstOrDefault(p => p.Id == id); dialogs.ApplyTheme(Session.Theme);
    }
    private void Filter()
    {
        var items = all.Where(p => PlayerRules.Matches(p, Search) && (Section == "Dashboard" || p.Status.ToString() == Section)
            && (TypeFilter == "All" || p.PlayerType == TypeFilter) && (BattingFilter == "All" || p.BattingStyle == BattingFilter)
            && (BowlingFilter == "All" || p.BowlingStyle == BowlingFilter) && (CityFilter == "All" || p.AreaCity == CityFilter)
            && (RoundFilter == "All" || p.AuctionRound.ToString() == RoundFilter) && (TeamFilter == "All" || p.TeamName == TeamFilter)).ToList();
        Players.Clear(); foreach (var p in items) Players.Add(p); Changed(nameof(ListTitle)); Changed(nameof(HasPlayers));
    }
    public async Task Initialize()
    {
        await Guard(() => { if (HasSession) { Refresh(); Notice = "Auction ready. Ctrl+F to search, S to sell, U for unsold."; } return Task.CompletedTask; });
        if (!HasSession) await Guard(New);
    }
    private Task New()
    {
        var names = dialogs.NewAuction(); if (names == null) return Task.CompletedTask;
        workspace.Create(names.Value.Tournament, names.Value.Auction, teamNames: names.Value.Teams, teamBudgetPaise: names.Value.BudgetPaise); Reset(); Refresh(); Notice = $"Auction created with {names.Value.Teams.Count} teams at {Money.Format(names.Value.BudgetPaise)} each. Import players to begin bidding."; return Task.CompletedTask;
    }
    private void Reset() { PhotoFailures.Clear(); Changed(nameof(HasPhotoFailures)); Selected = null; Search = ""; Section = "Dashboard"; TypeFilter = BattingFilter = BowlingFilter = CityFilter = RoundFilter = TeamFilter = "All"; }
    private Task Open()
    {
        var file = dialogs.OpenFile("Auction database|*.db"); if (file == null) return Task.CompletedTask;
        workspace.Select(file); Reset(); Refresh(); Notice = "Auction opened."; return Task.CompletedTask;
    }
    private Task OpenSavedAuction()
    {
        var previousPath = workspace.DatabasePath;
        var selectedAuction = dialogs.SelectAuction(workspace);
        if (selectedAuction == null)
        {
            if (workspace.DatabasePath.Length == 0) ClearWorkspace();
            else
            {
                if (!string.Equals(previousPath, workspace.DatabasePath, StringComparison.OrdinalIgnoreCase)) Reset();
                Refresh();
                if (previousPath.Length > 0 && !string.Equals(previousPath, workspace.DatabasePath, StringComparison.OrdinalIgnoreCase)) Notice = "Active auction deleted. Another saved auction is open.";
            }
            if (previousPath.Length > 0 && workspace.DatabasePath.Length == 0) Notice = "Active auction deleted. Create or open an auction.";
            return Task.CompletedTask;
        }
        workspace.Select(selectedAuction.DatabasePath); Reset(); Refresh(); Notice = "Auction opened."; return Task.CompletedTask;
    }
    private void ClearWorkspace()
    {
        Reset(); Session = null; all = []; Stats = Statistics.Calculate(all); Teams.Clear(); History.Clear();
        Types = BattingStyles = BowlingStyles = Cities = Rounds = TeamNames = ["All"];
        foreach (var property in new[] { nameof(Session), nameof(HasSession), nameof(Title), nameof(Subtitle), nameof(Stats), nameof(TotalValue), nameof(AverageValue), nameof(HighestValue), nameof(TotalPurse), nameof(TeamReadiness), nameof(PlayerReadiness), nameof(PhotoReadiness), nameof(LastSale), nameof(Leaders), nameof(Types), nameof(BattingStyles), nameof(BowlingStyles), nameof(Cities), nameof(Rounds), nameof(TeamNames) }) Changed(property);
        Filter();
    }
    private async Task Import()
    {
        var file = dialogs.OpenFile("Player registrations|*.xlsx;*.csv"); if (file == null) return;
        Notice = "Reading registrations…"; var rows = await Task.Run(() => importer.Preview(file));
        if (!dialogs.ImportPreview(rows)) { Notice = "Import cancelled."; return; }
        var count = await Task.Run(() => importer.Import(rows));
        var skipped = rows.Count - count;
        var importSummary = $"Import complete: {count} of {rows.Count} rows added or updated; {skipped} skipped.";
        var invalidRows = rows.Where(row => row.Error.Length > 0).ToList();
        if (invalidRows.Count > 0)
            importSummary += " Invalid rows: " + string.Join("; ", invalidRows.Select(row => $"Row {row.RowNumber} ({row.Player.FullName}): {row.Error}"));
        var keptRows = rows.Where(row => row.Error.Length == 0 && row.IsDuplicate && row.Resolution == DuplicateResolution.KeepExisting).ToList();
        if (keptRows.Count > 0)
            importSummary += " Kept existing: " + string.Join("; ", keptRows.Select(row => $"Row {row.RowNumber} ({row.Player.FullName})")) + ". Choose ImportAsNew in preview if these are distinct players.";
        Search = ""; Section = "Available"; TypeFilter = BattingFilter = BowlingFilter = CityFilter = RoundFilter = TeamFilter = "All";
        Refresh(); Notice = $"Import complete: {count} players added or updated. Caching available photos…";
        await CachePhotos(); Notice = $"{importSummary} {Notice}";
    }
    private Task Sell()
    {
        if (Selected == null) return Task.CompletedTask;
        if (Teams.Count == 0) { dialogs.Teams(auction, Session!); Refresh(); }
        if (!CheckReadiness()) return Task.CompletedTask;
        var sale = dialogs.Sale(Selected!, Teams.ToList()); if (sale == null) return Task.CompletedTask; var p = Selected!;
        if (!dialogs.Confirm($"Confirm sale\n\n{p.FullName}\n{sale.Value.Team.Name}\n{Money.Format(sale.Value.Paise)}\n\nSave this auction result?")) return Task.CompletedTask;
        auction.Sell(p.Id, sale.Value.Team.Id, sale.Value.Paise, sale.Value.Notes); Refresh(); Notice = $"✓ {p.FullName} sold to {sale.Value.Team.Name} for {Money.Format(sale.Value.Paise)}"; return Task.CompletedTask;
    }
    private Task Unsold()
    {
        if (Selected is { } p && dialogs.Confirm($"Mark {p.FullName} as UNSOLD in round {p.AuctionRound}?")) { auction.MarkUnsold(p.Id); Refresh(); Notice = $"{p.FullName} marked unsold. Available for the next re-auction."; }
        return Task.CompletedTask;
    }
    private Task NextRound()
    {
        if (dialogs.Confirm($"Start round {Session!.CurrentRound + 1}?\nAll unsold players will become available. Their history will be preserved.\nFinish all currently available players first.")) { var n = auction.StartNextRound(); Refresh(); Section = "Available"; Notice = $"Round {Session!.CurrentRound} started with {n} players."; } return Task.CompletedTask;
    }
    private Task Undo() { if (dialogs.Confirm("Undo last auction action?\n\n" + auction.LastActionDescription())) { auction.Undo(); Refresh(); Notice = "Action undone. An audit record has been added."; } return Task.CompletedTask; }
    private Task TeamsDialog() { dialogs.Teams(auction, Session!); Refresh(); return Task.CompletedTask; }
    private Task Settings() { var settings = dialogs.Settings(Session!); if (settings != null) { workspace.SaveSettings(settings); Refresh(); Notice = "Settings saved."; } return Task.CompletedTask; }
    private Task Edit() { if (Selected is { } p && dialogs.EditPlayer(p) is { } edit) { auction.SavePlayer(edit); Refresh(); Notice = "Player updated."; } return Task.CompletedTask; }
    private Task Add() { if (dialogs.EditPlayer(new()) is { } p) { auction.SavePlayer(p); Refresh(); Selected = all.FirstOrDefault(x => x.Id == p.Id); } return Task.CompletedTask; }
    private async Task ChangePhoto()
    {
        if (Selected == null) return; var id = Selected.Id; var file = dialogs.OpenFile(PhotoNormalizer.FileFilter); if (file == null) return;
        Notice = "Converting and saving photo…";
        await Task.Run(() => auction.SavePhoto(id, dialogs.LoadPhoto(file), true)); Refresh(); Notice = "Photo converted and saved offline, including in database backups.";
    }
    private async Task RotatePhoto()
    {
        if (Selected?.Photo is not { Length: > 0 } photo) return;
        var id = Selected.Id; var rotated = dialogs.RotatePhoto(photo);
        await Task.Run(() => auction.SaveRotatedPhoto(id, photo, rotated)); Refresh(); Notice = "Player photo rotated and saved. You can restore the original photo.";
    }
    private async Task RestorePhoto()
    {
        if (Selected == null || !CanRestorePhoto) return;
        var id = Selected.Id; await Task.Run(() => auction.RestoreOriginalPhoto(id)); Refresh(); Notice = "Original player photo restored.";
    }
    private async Task CachePhotos()
    {
        photoCancellation = new(); IsCachingPhotos = true;
        var token = photoCancellation.Token;
        try
        {
            var missing = all.Where(player => player.Photo is not { Length: > 0 } && !string.IsNullOrWhiteSpace(player.PlayerPhotoUrl)).ToList();
            PhotoFailures.Clear(); Changed(nameof(HasPhotoFailures));
            var completed = 0; var saved = 0; var failures = new List<(string Name, string Reason)>();
            PhotoProgressText = missing.Count == 0 ? $"{all.Count} players in auction. All linked player photos are already saved." : $"{all.Count} players in auction. Downloading photos: 0 of {missing.Count}; 0 photos saved.";
            if (missing.Count == 0) { Notice = PhotoProgressText; return; }
            foreach (var batch in missing.Chunk(4))
            {
                if (token.IsCancellationRequested) break;
                var pending = batch.Select(async player => (Player: player, Result: await images.FetchWithResultAsync(player.PlayerPhotoUrl, token))).ToList();
                while (pending.Count > 0)
                {
                    var finished = await Task.WhenAny(pending); pending.Remove(finished);
                    var result = await finished;
                    if (token.IsCancellationRequested && result.Result.Error == "Download cancelled.") continue;
                    if (result.Result.Bytes is { } bytes)
                    {
                        try { auction.SavePhoto(result.Player.Id, bytes, false); saved++; }
                        catch (Exception ex) { log.Write($"Photo processing failed for player {result.Player.Id}", ex); var reason = ex is IOException or UnauthorizedAccessException ? "Could not save photo. Check disk space and folder permissions, then retry." : "Could not decode or save this image. Convert it to JPG or PNG and choose Photo, or check the application log."; failures.Add((result.Player.FullName, reason)); PhotoFailures.Add(new(result.Player.Id, result.Player.FullName, reason)); }
                    }
                    else if (result.Result.Error is { } error && error != "Download cancelled.") { failures.Add((result.Player.FullName, error)); PhotoFailures.Add(new(result.Player.Id, result.Player.FullName, error)); }
                    completed++;
                    PhotoProgressText = $"{all.Count} players in auction. Downloading photos: {completed} of {missing.Count}; {saved} photos saved.";
                    Notice = PhotoProgressText;
                }
            }
            Refresh();
            var cancelled = token.IsCancellationRequested ? " Download cancelled." : "";
            Changed(nameof(HasPhotoFailures));
            var failureDetails = failures.Count == 0 ? "" : $" {failures.Count} failed. Open Photo failures for player names, reasons and next steps.";
            Notice = $"{all.Count} players in auction. Photo download finished: {saved} of {missing.Count} photos saved. {all.Count(player => string.IsNullOrWhiteSpace(player.PlayerPhotoUrl))} players have no photo link and remain in the auction.{cancelled}{failureDetails}";
        }
        finally { Changed(nameof(HasPhotoFailures)); IsCachingPhotos = false; photoCancellation.Dispose(); photoCancellation = null; }
    }
    private async Task Export()
    {
        var choice = dialogs.Choose("Export Excel", ["Complete auction report", "All players", "Sold players", "Unsold players", "Current filtered list"]); if (choice == null) return;
        var list = choice switch { "Sold players" => all.Where(player => player.Status == AuctionStatus.Sold).ToList(), "Unsold players" => all.Where(player => player.Status == AuctionStatus.Unsold).ToList(), "Current filtered list" => Players.ToList(), _ => all.ToList() };
        if (!dialogs.Confirm($"Export {choice}?\n\nPlayers: {list.Count}\nTournament: {Session!.TournamentName}\n\nContinue to choose the Excel file location?")) return;
        var file = dialogs.SaveFile("Excel workbook|*.xlsx", $"Auction_Report_{DateTime.Now:yyyyMMdd}.xlsx", Session.ExportLocation); if (file == null) return;
        Notice = "Generating Excel…"; await Task.Run(() => exporter.Export(file, list, choice == "Complete auction report")); Notice = "Excel saved: " + file;
    }
    private async Task PowerPoint()
    {
        var choice = dialogs.Choose("Generate PowerPoint", ["All players", "All available players", "All sold players", "All unsold players", "Unsold in current round", "Select unsold players", "Selected team squad"]); if (choice == null) return;
        var list = choice switch
        {
            "All players" => all.ToList(),
            "All available players" => all.Where(player => player.Status == AuctionStatus.Available).ToList(),
            "All sold players" => all.Where(player => player.Status == AuctionStatus.Sold).ToList(),
            _ => all.Where(player => player.Status == AuctionStatus.Unsold && (choice != "Unsold in current round" || player.AuctionRound == Session!.CurrentRound)).ToList()
        };
        if (choice == "Select unsold players") { var selectedPlayers = dialogs.SelectPlayers(list); if (selectedPlayers == null) return; list = selectedPlayers; }
        if (choice == "Selected team squad")
        {
            var name = dialogs.Choose("Choose team", Teams.Select(team => team.Name).ToArray()); if (name == null) return;
            list = all.Where(player => player.Status == AuctionStatus.Sold && player.TeamName == name).ToList();
        }
        if (list.Count == 0) throw new InvalidOperationException("There are no players for this selection.");
        var orderedPlayers = dialogs.ArrangePresentationPlayers(list, choice);
        if (orderedPlayers == null) return;
        list = orderedPlayers;
        var defaultName = choice switch
        {
            "Selected team squad" => "Team_Squad.pptx",
            "All players" => $"All_Players_Round_{Session!.CurrentRound}.pptx",
            "All sold players" => $"Sold_Players_Round_{Session!.CurrentRound}.pptx",
            "All available players" => $"Available_Players_Round_{Session!.CurrentRound}.pptx",
            _ => $"Unsold_Players_Round_{Session!.CurrentRound}.pptx"
        };
        var file = dialogs.SaveFile("PowerPoint presentation|*.pptx", defaultName, Session!.PptLocation); if (file == null) return;
        Notice = "Generating presentation…"; await Task.Run(() => ppt.Generate(file, list, Session.TournamentName, Session.CurrentRound)); Notice = $"PowerPoint saved: {list.Count} player slides. {file}";
    }
    private async Task TeamReportPdf()
    {
        var sold = all.Where(player => player.Status == AuctionStatus.Sold).ToList();
        if (sold.Count == 0) throw new InvalidOperationException("There are no purchased players to include in the team report.");
        var purchasingTeams = auction.Teams().Where(team => sold.Any(player => player.TeamId == team.Id)).ToList();
        if (!dialogs.Confirm($"Generate the team purchase PDF?\n\nTeams with purchases: {purchasingTeams.Count}\n{string.Join(", ", purchasingTeams.Select(team => team.Name))}\nPurchased players: {sold.Count}\nPhotos available: {sold.Count(player => player.Photo is { Length: > 0 })}\n\nContinue to choose the PDF file location?")) return;
        var file = dialogs.SaveFile("PDF team report|*.pdf", $"Team_Purchases_{DateTime.Now:yyyyMMdd}.pdf", Session!.ExportLocation); if (file == null) return;
        Notice = "Generating team report PDF…";
        await Task.Run(() => teamReportPdf.Generate(file, purchasingTeams, sold, Session.TournamentName));
        Notice = $"Team report PDF saved: {sold.Count} purchased players. {file}";
    }
    private Task TeamPosterImage()
    {
        var sold = all.Where(player => player.Status == AuctionStatus.Sold).ToList();
        if (sold.Count == 0) throw new InvalidOperationException("There are no purchased players to include in a team poster.");
        var teams = auction.Teams().Where(team => sold.Any(player => player.TeamId == team.Id)).ToList();
        var teamName = dialogs.Choose("Download team poster", teams.Select(team => team.Name).ToArray()); if (teamName == null) return Task.CompletedTask;
        var team = teams.Single(item => item.Name == teamName); var teamPlayers = sold.Where(player => player.TeamId == team.Id).ToList();
        var design = dialogs.ChoosePosterDesign(teamPosterImage, team, teamPlayers, Session!);
        if (design == null) return Task.CompletedTask;
        var safeTeamName = string.Join("_", team.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var file = dialogs.SaveFile("PNG image|*.png", $"{safeTeamName}_Team_Poster.png", Session!.ExportLocation); if (file == null) return Task.CompletedTask;
        Notice = $"Generating {team.Name} team poster…";
        teamPosterImage.Generate(file, team, sold, Session.TournamentName, design.Value, Session.AuctionName);
        Notice = $"Team poster image saved: {team.Name}, {teamPlayers.Count} players. {file}";
        return Task.CompletedTask;
    }
    private async Task Backup()
    {
        var file = dialogs.SaveFile("Auction database|*.db", $"Auction_Backup_{DateTime.Now:yyyy-MM-dd}.db", ""); if (file == null) return;
        await Task.Run(() => backup.Backup(file)); Notice = "Portable database backup saved: " + file;
    }
    private async Task Restore()
    {
        var file = dialogs.OpenFile("Auction backup|*.db"); if (file == null) return;
        var destination = Path.Combine(workspace.Root, $"Restored_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..6]}.db");
        await Task.Run(() => backup.Restore(file, destination)); Reset(); Refresh(); Notice = "Backup restored as a separate auction. Previous auctions remain intact.";
    }
}

public sealed record PhotoFailure(int PlayerId, string Name, string Reason);
internal sealed class DelegateCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
}
