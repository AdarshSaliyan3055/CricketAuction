using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using CricketAuction.Domain;
using CricketAuction.Infrastructure;
using Microsoft.Win32;

namespace CricketAuction.App;

public sealed class FixtureLine : INotifyPropertyChanged
{
    public FixtureRecord Fixture { get; }
    private readonly IReadOnlyDictionary<int, string> teamNames;
    public IReadOnlyList<Team> WinnerOptions { get; }
    private FixtureStatus? pendingStatus;
    public FixtureStatus? PendingStatus
    {
        get => pendingStatus;
        set
        {
            if (pendingStatus == value) return;
            pendingStatus = value;
            PropertyChanged?.Invoke(this, new(nameof(PendingStatus)));
            PropertyChanged?.Invoke(this, new(nameof(CanChooseWinner)));
            PropertyChanged?.Invoke(this, new(nameof(WinnerLabel)));
        }
    }
    public int? PendingWinnerTeamId { get; set; }
    public bool CanEditResult => !Fixture.IsBye && Fixture.TeamAId.HasValue && Fixture.TeamBId.HasValue;
    public bool CanChooseWinner => CanEditResult && (Fixture.Status == FixtureStatus.Completed || PendingStatus == FixtureStatus.Completed);
    private string inlineMessage = "";
    private string warning = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string InlineMessage
    {
        get => inlineMessage;
        private set
        {
            if (inlineMessage == value) return;
            inlineMessage = value;
            PropertyChanged?.Invoke(this, new(nameof(InlineMessage)));
            PropertyChanged?.Invoke(this, new(nameof(Warning)));
        }
    }
    public string Stage => Fixture.PoolName.Length > 0 ? Fixture.PoolName : Fixture.RoundName switch { "Semi-finals" or "Semi-final" => "Semifinals", "3rd place" => "Third-place match", _ => Fixture.RoundName };
    public string StageLabel => Stage;
    public string MatchLabel => $"Match {MatchNumber}";
    public string Warning
    {
        get => inlineMessage.Length > 0 ? inlineMessage : warning;
        init => warning = value;
    }
    public string QualificationLabel => Fixture.IsBye || (Fixture.TeamAId.HasValue && Fixture.TeamBId.HasValue) ? "" :
        Fixture.TeamASeed.Contains("Pool") || Fixture.TeamBSeed.Contains("Pool") ? "Pending pool results / tie-break" : "Pending preceding results";
    public int MatchNumber => Fixture.MatchNumber;
    public string RoundName => Fixture.RoundName;
    public string TeamAName => Fixture.TeamAId is int teamA && teamNames.TryGetValue(teamA, out var nameA) ? nameA : Fixture.TeamASeed.Length > 0 ? Fixture.TeamASeed : "TBD";
    public string TeamBName => Fixture.TeamBId is int teamB && teamNames.TryGetValue(teamB, out var nameB) ? nameB : Fixture.TeamBSeed.Length > 0 ? Fixture.TeamBSeed : "TBD";
    public string Matchup => Fixture.IsBye ? $"{TeamAName}  ·  BYE" : $"{TeamAName}  vs  {TeamBName}";
    public string DateLabel => Fixture.ScheduledAt.ToString("dd MMM · h:mm tt", CultureInfo.GetCultureInfo("en-GB"));
    public string Ground => string.IsNullOrWhiteSpace(Fixture.Ground) ? "Not assigned" : Fixture.Ground;
    public string StatusLabel => Fixture.IsBye ? "Bye" : Fixture.Status switch
    {
        FixtureStatus.InProgress => "In progress",
        FixtureStatus.Completed => "Completed",
        FixtureStatus.Abandoned => "Abandoned",
        _ => "Scheduled"
    };
    public string WinnerLabel => Fixture.WinnerTeamId is int winner && teamNames.TryGetValue(winner, out var winnerName)
        ? winnerName : PendingStatus == FixtureStatus.Completed ? "Choose winner" : "—";
    public string ScoreSummary => string.IsNullOrWhiteSpace(Fixture.Result) ? "—" : Fixture.Result;
    public string ResultLabel => Fixture.WinnerTeamId is int winner && teamNames.TryGetValue(winner, out var winnerName)
        ? string.IsNullOrWhiteSpace(Fixture.Result) ? $"Winner · {winnerName}" : $"{winnerName} · {Fixture.Result}"
        : Fixture.Result;

    public FixtureLine(FixtureRecord fixture, IReadOnlyList<Team> teams)
    {
        Fixture = fixture;
        teamNames = teams.ToDictionary(team => team.Id, team => team.Name);
        WinnerOptions = teams.Where(team => team.Id == fixture.TeamAId || team.Id == fixture.TeamBId).ToList();
        PendingWinnerTeamId = fixture.WinnerTeamId;
    }

    public void ShowInlineMessage(string message) => InlineMessage = message;
}

public partial class FixturesWindow : Window, INotifyPropertyChanged
{
    private sealed record ScheduleFormatOption(string Label, TournamentFormat Format);

    private readonly FixtureService fixtureService;
    private readonly AuctionService auction;
    private readonly Workspace workspace;
    private FixtureSchedule schedule = new();
    private List<Team> teams = [];
    private FixtureLine? selectedFixture;
    private bool updatingFixtures;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? HomeRequested;
    public ObservableCollection<FixtureLine> VisibleFixtures { get; } = [];
    public Array StatusOptions => Enum.GetValues<FixtureStatus>();
    public string WarningSummary { get; private set; } = "";
    public string TournamentName => workspace.DatabasePath.Length == 0 ? "No active tournament" : workspace.Session().TournamentName;
    public int TotalCount => schedule.Fixtures.Count;
    public int UpcomingCount => schedule.Fixtures.Count(fixture => fixture.Status == FixtureStatus.Scheduled && !fixture.IsBye);
    public int LiveCount => schedule.Fixtures.Count(fixture => fixture.Status == FixtureStatus.InProgress);
    public int CompletedCount => schedule.Fixtures.Count(fixture => fixture.Status == FixtureStatus.Completed);
    public int VisibleCount => VisibleFixtures.Count;
    public string EmptyMessage => TotalCount == 0
        ? "No fixtures yet. Generate a league or knockout schedule to get started."
        : "No fixtures match your search.";
    public FixtureLine? SelectedFixture
    {
        get => selectedFixture;
        set { if (ReferenceEquals(selectedFixture, value)) return; selectedFixture = value; Changed(); Changed(nameof(HasSelection)); Changed(nameof(CanDelete)); }
    }
    public bool HasSelection => SelectedFixture != null;
    public bool CanDelete => SelectedFixture != null && schedule.Format == TournamentFormat.League && !SelectedFixture.Fixture.IsBye;

    public FixturesWindow(FixtureService fixtureService, AuctionService auction, Workspace workspace)
    {
        InitializeComponent();
        this.fixtureService = fixtureService;
        this.auction = auction;
        this.workspace = workspace;
        DataContext = this;
        Activated += (_, _) => Reload();
        Reload();
    }

    private void Reload()
    {
        if (workspace.DatabasePath.Length == 0) return;
        teams = auction.Teams();
        schedule = fixtureService.Load();
        Changed(nameof(TournamentName));
        RefreshView();
    }

    private void RefreshView()
    {
        var selectedId = SelectedFixture?.Fixture.Id;
        var warnings = FixtureService.GetConflictWarnings(schedule);
        var lastEnd = schedule.Fixtures.Where(f => !f.IsBye).Select(f => f.ScheduledAt.AddMinutes(f.DurationMinutes)).DefaultIfEmpty().Max();
        WarningSummary = $"{warnings.Count(pair => pair.Value.Length > 0)} matches with warnings · Minimum rest: {schedule.MinimumRestMinutes} min."
            + (lastEnd == default ? "" : $" Estimated finish: {lastEnd:dd MMM · h:mm tt}.") + " Unconfirmed teams cannot be checked.";
        Changed(nameof(WarningSummary));
        var term = SearchBox.Text.Trim();
        updatingFixtures = true;
        VisibleFixtures.Clear();
        foreach (var fixture in schedule.Fixtures.OrderBy(fixture => fixture.ScheduledAt).ThenBy(fixture => fixture.MatchNumber))
        {
            var line = new FixtureLine(fixture, teams) { Warning = warnings.GetValueOrDefault(fixture.Id, "") };
            if (term.Length > 0 && !new[] { line.Matchup, line.RoundName, line.Ground, line.ResultLabel, line.StatusLabel }
                    .Any(value => value.Contains(term, StringComparison.CurrentCultureIgnoreCase))) continue;
            VisibleFixtures.Add(line);
        }
        updatingFixtures = false;
        SelectedFixture = VisibleFixtures.FirstOrDefault(line => line.Fixture.Id == selectedId);
        Changed(nameof(TotalCount)); Changed(nameof(UpcomingCount)); Changed(nameof(LiveCount)); Changed(nameof(CompletedCount));
        Changed(nameof(VisibleCount)); Changed(nameof(EmptyMessage));
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) RefreshView();
    }

    private void FixtureGridBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not FixtureLine line) return;
        var column = e.Column.Header?.ToString();
        if (column == "STATUS" && line.Fixture.Status == FixtureStatus.Completed) e.Cancel = true;
        else if (column == "WINNER" && !line.CanChooseWinner) e.Cancel = true;
    }

    private void FixtureStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFixtures || sender is not ComboBox { DataContext: FixtureLine line, SelectedItem: FixtureStatus status }) return;
        if (status == line.Fixture.Status && line.PendingStatus != FixtureStatus.Completed) return;
        if (status == FixtureStatus.Completed && !(line.PendingWinnerTeamId ?? line.Fixture.WinnerTeamId).HasValue)
        {
            line.PendingStatus = FixtureStatus.Completed;
            line.ShowInlineMessage("Select a winner to complete this match.");
            return;
        }
        SaveInlineResult(line, status, status == FixtureStatus.Completed ? line.PendingWinnerTeamId ?? line.Fixture.WinnerTeamId : null);
    }

    private void FixtureWinnerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFixtures || sender is not ComboBox { DataContext: FixtureLine line } combo) return;
        var selectedWinner = combo.SelectedValue is int teamId ? teamId : (int?)null;
        if (selectedWinner == line.PendingWinnerTeamId) return;
        line.PendingWinnerTeamId = selectedWinner;
        var status = line.PendingStatus ?? line.Fixture.Status;
        if (status != FixtureStatus.Completed) return;
        if (!line.PendingWinnerTeamId.HasValue)
        {
            line.PendingWinnerTeamId = line.Fixture.WinnerTeamId;
            combo.SelectedValue = line.Fixture.WinnerTeamId;
            line.ShowInlineMessage("A winner is required while the match is completed.");
            return;
        }
        SaveInlineResult(line, FixtureStatus.Completed, line.PendingWinnerTeamId);
    }

    private void SaveInlineResult(FixtureLine line, FixtureStatus status, int? winnerTeamId)
    {
        try
        {
            FixtureService.RecordResult(schedule, line.Fixture, status, line.Fixture.Result, winnerTeamId);
            fixtureService.Save(schedule);
            line.PendingStatus = null;
            line.ShowInlineMessage("");
            RefreshView();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not update match", MessageBoxButton.OK, MessageBoxImage.Warning);
            Reload();
        }
    }

    private void GenerateSchedule(object sender, RoutedEventArgs e)
    {
        teams = auction.Teams();
        if (teams.Count < 2) { MessageBox.Show(this, "Create at least two teams in the Auction feature before generating a schedule.", "Fixtures", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var options = PromptSchedule();
        if (options == null) return;
        try
        {
            var generated = FixtureService.Generate(teams, options.Value.Format, options.Value.StartDate, options.Value.FirstMatchTime,
                options.Value.DaysBetweenRounds, options.Value.Grounds, options.Value.DoubleRoundRobin, options.Value.PoolAssignments, options.Value.MatchMinutes);
            if (schedule.Fixtures.Count > 0 && MessageBox.Show(this,
                $"Replace {schedule.Fixtures.Count} existing fixtures with {generated.Fixtures.Count} new fixtures? The current schedule will be deleted. This cannot be undone.",
                "Replace fixtures", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            schedule = generated;
            fixtureService.Save(schedule);
            RefreshView();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not generate fixtures", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private (TournamentFormat Format, DateTime StartDate, TimeSpan FirstMatchTime, int DaysBetweenRounds, List<string> Grounds, bool DoubleRoundRobin, IReadOnlyDictionary<string, IReadOnlyList<int>> PoolAssignments, int MatchMinutes)? PromptSchedule()
    {
        var dialog = Dialog("Generate schedule", 540, 690);
        var form = new StackPanel { Margin = new Thickness(24) };
        dialog.Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        form.Children.Add(Heading("Schedule format"));
        var formatOptions = new List<ScheduleFormatOption>
        {
            new("League · all teams", TournamentFormat.League),
            new("Knockout", TournamentFormat.Knockout)
        };
        if (teams.Count >= 4) formatOptions.Add(new("League · pools", TournamentFormat.PoolPlay));
        var format = new ComboBox { ItemsSource = formatOptions, DisplayMemberPath = nameof(ScheduleFormatOption.Label), SelectedIndex = teams.Count >= 4 ? 2 : 0, MinHeight = 34 };
        form.Children.Add(format);
        form.Children.Add(Label("League · all teams: every team plays every other team. Pools + Playoffs: assign teams to pools; each pool plays a round robin. With two pools, the top two advance to cross-pool semifinals, then final and third-place match. Knockout creates an elimination bracket."));
        form.Children.Add(Label("Double round robin means each team plays every other team twice instead of once."));
        var poolPanel = new StackPanel { Visibility = teams.Count >= 4 ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        form.Children.Add(poolPanel);
        var poolCountOptions = Enumerable.Range(2, Math.Max(1, teams.Count / 2 - 1)).ToList();
        var poolCount = new ComboBox { ItemsSource = poolCountOptions, SelectedItem = 2, MinHeight = 34 };
        var selectors = new Dictionary<int, ComboBox>();
        poolPanel.Children.Add(Label("Number of pools"));
        poolPanel.Children.Add(poolCount);
        var assignmentPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        poolPanel.Children.Add(Label("Assign each team to a pool"));
        poolPanel.Children.Add(assignmentPanel);
        void RebuildPoolAssignments()
        {
            var count = poolCount.SelectedItem is int selectedCount ? selectedCount : 2;
            var names = Enumerable.Range(0, count).Select(index => PoolName(index)).ToList();
            var prior = selectors.ToDictionary(pair => pair.Key, pair => pair.Value.SelectedItem?.ToString() ?? "");
            var defaults = FixtureService.DefaultPoolAssignments(teams, count).SelectMany(pair => pair.Value.Select(id => (id, pair.Key))).ToDictionary(item => item.id, item => item.Key);
            selectors.Clear(); assignmentPanel.Children.Clear();
            foreach (var team in teams.OrderBy(team => team.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var label = new TextBlock { Text = team.Name, VerticalAlignment = VerticalAlignment.Center, MinWidth = 180 };
                var selector = new ComboBox { ItemsSource = names, MinWidth = 130, MinHeight = 30 };
                selector.SelectedItem = names.Contains(prior.GetValueOrDefault(team.Id, "")) ? prior[team.Id] : defaults[team.Id];
                DockPanel.SetDock(selector, Dock.Right); row.Children.Add(selector); row.Children.Add(label);
                assignmentPanel.Children.Add(row); selectors[team.Id] = selector;
            }
        }
        poolCount.SelectionChanged += (_, _) => { if (poolCount.SelectedItem != null) RebuildPoolAssignments(); };
        if (teams.Count >= 4) RebuildPoolAssignments();
        form.Children.Add(Label("First match date"));
        var date = new DatePicker { SelectedDate = DateTime.Today, MinHeight = 32 };
        form.Children.Add(date);
        form.Children.Add(Label("First match time (24-hour clock)"));
        var time = new TextBox { Text = "09:00" }; form.Children.Add(time);
        var singleDay = new CheckBox { Content = "Finish in one day", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
        form.Children.Add(singleDay);
        form.Children.Add(Label("Match duration (minutes)"));
        var matchDuration = new TextBox { Text = "40" }; form.Children.Add(matchDuration);
        form.Children.Add(Label("Pools alternate where possible. Each team gets at least one match-length rest. Breaks are added before playoff stages and when no rested team is available."));
        form.Children.Add(Label("Days between rounds (multi-day only)"));
        var interval = new TextBox { Text = "1", IsEnabled = false }; form.Children.Add(interval);
        singleDay.Checked += (_, _) => interval.IsEnabled = false;
        singleDay.Unchecked += (_, _) => interval.IsEnabled = true;
        var doubleRound = new CheckBox { Content = "Add return fixtures (double round robin)", Margin = new Thickness(0, 14, 0, 4) };
        form.Children.Add(doubleRound);
        void UpdateFormatOptions()
        {
            var selectedFormat = (format.SelectedItem as ScheduleFormatOption)?.Format;
            doubleRound.IsEnabled = selectedFormat == TournamentFormat.League;
            poolPanel.Visibility = selectedFormat == TournamentFormat.PoolPlay ? Visibility.Visible : Visibility.Collapsed;
        }
        format.SelectionChanged += (_, _) => UpdateFormatOptions();
        UpdateFormatOptions();
        form.Children.Add(Label("Grounds (one per line; fixtures rotate between them)"));
        var grounds = new TextBox { AcceptsReturn = true, Height = 90, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        form.Children.Add(grounds);
        var footer = Footer(dialog, "Generate fixtures"); form.Children.Add(footer.Panel);
        (TournamentFormat Format, DateTime StartDate, TimeSpan FirstMatchTime, int DaysBetweenRounds, List<string> Grounds, bool DoubleRoundRobin, IReadOnlyDictionary<string, IReadOnlyList<int>> PoolAssignments, int MatchMinutes)? result = null;
        footer.Confirm.Click += (_, _) =>
        {
            var days = 0;
            if (date.SelectedDate is not DateTime start || !TimeSpan.TryParseExact(time.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var startTime)
                || !int.TryParse(matchDuration.Text, out var matchMinutes) || matchMinutes < 1 || matchMinutes > 1440
                || (singleDay.IsChecked != true && (!int.TryParse(interval.Text, out days) || days < 1)))
            {
                MessageBox.Show(dialog, "Enter a valid date, time (HH:mm), match duration (1–1440 minutes), and at least one day between rounds for multi-day schedules.", "Schedule details", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var assignments = selectors.Where(pair => pair.Value.SelectedItem is string)
                .GroupBy(pair => pair.Value.SelectedItem!.ToString()!)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<int>)group.Select(pair => pair.Key).ToList());
            if (format.SelectedItem is ScheduleFormatOption { Format: TournamentFormat.PoolPlay }
                && assignments.Any(pool => pool.Value.Count < 2))
            {
                MessageBox.Show(dialog, "Every pool must have at least two teams. Change the team assignments or number of pools.", "Pool assignments", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            result = (((ScheduleFormatOption)format.SelectedItem).Format, start, startTime, days,
                grounds.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList(),
                doubleRound.IsChecked == true, assignments, matchMinutes);
            dialog.DialogResult = true;
        };
        return dialog.ShowDialog() == true ? result : null;
    }

    private static string PoolName(int index) => $"Pool {(char)('A' + index)}";

    private void AddFixture(object sender, RoutedEventArgs e)
    {
        if ((schedule.Format is TournamentFormat.Knockout or TournamentFormat.PoolPlay) && schedule.Fixtures.Count > 0)
        {
            MessageBox.Show(this, "Manual matches can be added to league schedules. Bracket and pool schedules keep fixed qualification slots so advancement remains correct.", "Add match", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (teams.Count < 2) teams = auction.Teams();
        var fixture = new FixtureRecord
        {
            MatchNumber = schedule.Fixtures.Count + 1, Round = Math.Max(1, schedule.Fixtures.Select(item => item.Round).DefaultIfEmpty(0).Max()),
            ScheduledAt = DateTime.Today.AddHours(9)
        };
        fixture.RoundName = $"League · Round {fixture.Round}";
        if (EditFixtureDialog(fixture, true))
        {
            schedule.Format = TournamentFormat.League;
            schedule.Fixtures.Add(fixture);
            SaveAndRefresh();
        }
    }

    private bool EditFixtureDialog(FixtureRecord fixture, bool isNew)
    {
        if (teams.Count < 2) { MessageBox.Show(this, "At least two teams are required.", "Fixtures", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        var dialog = Dialog(isNew ? "Add match" : "Edit fixture", 540, 720);
        var form = new StackPanel { Margin = new Thickness(24) };
        dialog.Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        form.Children.Add(Heading(isNew ? "New fixture" : $"Match {fixture.MatchNumber}"));
        var name = new TextBox { Text = fixture.RoundName, IsReadOnly = !isNew && schedule.Format != TournamentFormat.League };
        form.Children.Add(Label("Round / stage")); form.Children.Add(name);
        ComboBox? teamA = null; ComboBox? teamB = null;
        var teamsEditable = isNew || schedule.Format == TournamentFormat.League;
        if (teamsEditable)
        {
            form.Children.Add(Label("Team 1")); teamA = new ComboBox { ItemsSource = teams, DisplayMemberPath = "Name", SelectedItem = teams.FirstOrDefault(team => team.Id == fixture.TeamAId), MinHeight = 34 }; form.Children.Add(teamA);
            form.Children.Add(Label("Team 2")); teamB = new ComboBox { ItemsSource = teams, DisplayMemberPath = "Name", SelectedItem = teams.FirstOrDefault(team => team.Id == fixture.TeamBId), MinHeight = 34 }; form.Children.Add(teamB);
        }
        else form.Children.Add(Label($"Teams: {new FixtureLine(fixture, teams).Matchup}"));
        var date = new DatePicker { SelectedDate = fixture.ScheduledAt.Date, MinHeight = 32 };
        form.Children.Add(Label("Date")); form.Children.Add(date);
        var time = new TextBox { Text = fixture.ScheduledAt.ToString("HH:mm", CultureInfo.InvariantCulture) };
        form.Children.Add(Label("Time (24-hour clock)")); form.Children.Add(time);
        var ground = new TextBox { Text = fixture.Ground };
        form.Children.Add(Label("Ground")); form.Children.Add(ground);
        var duration = new TextBox { Text = fixture.DurationMinutes.ToString() };
        form.Children.Add(Label("Expected duration (minutes, for conflict checks)")); form.Children.Add(duration);
        var status = new ComboBox { ItemsSource = Enum.GetValues<FixtureStatus>(), SelectedItem = fixture.Status, MinHeight = 34 };
        form.Children.Add(Label("Match status")); form.Children.Add(status);
        var result = new TextBox { Text = fixture.Result, AcceptsReturn = true, Height = 64, TextWrapping = TextWrapping.Wrap };
        form.Children.Add(Label("Result / score summary")); form.Children.Add(result);
        var participants = teams.Where(team => team.Id == fixture.TeamAId || team.Id == fixture.TeamBId).ToList();
        var winner = new ComboBox { ItemsSource = participants, DisplayMemberPath = "Name", SelectedItem = teams.FirstOrDefault(team => team.Id == fixture.WinnerTeamId), MinHeight = 34 };
        void RefreshWinnerChoices()
        {
            if (teamA == null || teamB == null) return;
            var currentWinnerId = (winner.SelectedItem as Team)?.Id;
            var choices = new[] { teamA.SelectedItem as Team, teamB.SelectedItem as Team }.Where(team => team != null).Cast<Team>().ToList();
            winner.ItemsSource = choices;
            winner.SelectedItem = choices.FirstOrDefault(team => team.Id == currentWinnerId);
        }
        if (teamA != null) teamA.SelectionChanged += (_, _) => RefreshWinnerChoices();
        if (teamB != null) teamB.SelectionChanged += (_, _) => RefreshWinnerChoices();
        form.Children.Add(Label("Winner (required when completed)")); form.Children.Add(winner);
        var footer = Footer(dialog, "Save fixture"); form.Children.Add(footer.Panel);
        var saved = false;
        footer.Confirm.Click += (_, _) =>
        {
            if (!int.TryParse(duration.Text, out var durationMinutes) || durationMinutes < 1 || durationMinutes > 1440)
            {
                MessageBox.Show(dialog, "Enter a duration between 1 and 1440 minutes.", "Fixture duration"); return;
            }
            if (date.SelectedDate is not DateTime day || !TimeSpan.TryParseExact(time.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var startTime))
            {
                MessageBox.Show(dialog, "Enter a valid date and time in HH:mm format.", "Fixture details", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (teamA != null && teamB != null && (teamA.SelectedItem is not Team home || teamB.SelectedItem is not Team away || home.Id == away.Id))
            {
                MessageBox.Show(dialog, "Choose two different teams.", "Fixture teams", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var newStatus = (FixtureStatus)status.SelectedItem;
            if (newStatus == FixtureStatus.Completed && winner.SelectedItem is not Team)
            {
                MessageBox.Show(dialog, "Select the winner for a completed match.", "Fixture result", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (teamA?.SelectedItem is Team homeTeam && teamB?.SelectedItem is Team awayTeam) { fixture.TeamAId = homeTeam.Id; fixture.TeamBId = awayTeam.Id; }
            fixture.RoundName = string.IsNullOrWhiteSpace(name.Text) ? "League · Round 1" : name.Text.Trim();
            fixture.ScheduledAt = day.Date.Add(startTime); fixture.Ground = ground.Text.Trim();
            fixture.DurationMinutes = durationMinutes;
            if ((schedule.Format is TournamentFormat.Knockout or TournamentFormat.PoolPlay) && !isNew)
                FixtureService.RecordResult(schedule, fixture, newStatus, result.Text, (winner.SelectedItem as Team)?.Id);
            else
            {
                fixture.Status = newStatus; fixture.Result = result.Text.Trim();
                fixture.WinnerTeamId = newStatus == FixtureStatus.Completed ? (winner.SelectedItem as Team)?.Id : null;
            }
            saved = true;
            dialog.DialogResult = true;
        };
        return dialog.ShowDialog() == true && saved;
    }

    private void DeleteFixture(object sender, RoutedEventArgs e)
    {
        if (SelectedFixture is not { } line || !CanDelete) return;
        if (MessageBox.Show(this, $"Delete match {line.MatchNumber}: {line.Matchup}?", "Delete fixture", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        schedule.Fixtures.Remove(line.Fixture);
        for (var index = 0; index < schedule.Fixtures.Count; index++) schedule.Fixtures[index].MatchNumber = index + 1;
        SaveAndRefresh();
    }

    private void ExportCsv(object sender, RoutedEventArgs e)
    {
        if (schedule.Fixtures.Count == 0) { MessageBox.Show(this, "There are no fixtures to export.", "Export fixtures", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new SaveFileDialog { Filter = "CSV file|*.csv", FileName = $"{SafeFileName(TournamentName)}_Fixtures.csv", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        var names = teams.ToDictionary(team => team.Id, team => team.Name);
        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        var lines = new List<string> { "Match,Round,Pool,Team 1,Team 2,Date,Time,Ground,Status,Result,Winner" };
        foreach (var fixture in schedule.Fixtures.OrderBy(item => item.ScheduledAt).ThenBy(item => item.MatchNumber))
        {
            var team1 = fixture.TeamAId is int idA && names.TryGetValue(idA, out var nameA) ? nameA : fixture.TeamASeed.Length > 0 ? fixture.TeamASeed : "TBD";
            var team2 = fixture.TeamBId is int idB && names.TryGetValue(idB, out var nameB) ? nameB : fixture.IsBye ? "Bye" : fixture.TeamBSeed.Length > 0 ? fixture.TeamBSeed : "TBD";
            var winnerName = fixture.WinnerTeamId is int idW && names.TryGetValue(idW, out var nameW) ? nameW : "";
            lines.Add(string.Join(",", fixture.MatchNumber, Csv(fixture.RoundName), Csv(fixture.PoolName), Csv(team1), Csv(team2), Csv(fixture.ScheduledAt.ToString("yyyy-MM-dd")), Csv(fixture.ScheduledAt.ToString("HH:mm")), Csv(fixture.Ground), Csv(fixture.Status.ToString()), Csv(fixture.Result), Csv(winnerName)));
        }
        File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, lines), new UTF8Encoding(true));
    }

    private void SaveAndRefresh()
    {
        fixtureService.Save(schedule);
        RefreshView();
    }

    private void PrintSchedule(object sender, RoutedEventArgs e)
    {
        if (schedule.Fixtures.Count == 0) { MessageBox.Show(this, "There are no fixtures to print.", "Print schedule"); return; }
        var print = new PrintDialog();
        if (print.ShowDialog() != true) return;
        try
        {
            var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 10, Foreground = Brushes.Black,
                PageWidth = print.PrintableAreaWidth, PageHeight = print.PrintableAreaHeight, PagePadding = new Thickness(28), ColumnWidth = double.PositiveInfinity };
            document.Blocks.Add(new Paragraph(new Run(TournamentName)) { FontSize = 20, FontWeight = FontWeights.Bold });
            document.Blocks.Add(new Paragraph(new Run("Full tournament schedule · Local times · " + DateTime.Today.ToString("dd MMM yyyy"))));
            var warnings = FixtureService.GetConflictWarnings(schedule);
            foreach (var group in schedule.Fixtures.OrderBy(f => f.ScheduledAt).ThenBy(f => f.MatchNumber)
                .Select(f => new FixtureLine(f, teams)).GroupBy(line => "Playing order"))
            {
                document.Blocks.Add(new Paragraph(new Run(group.Key)) { FontSize = 14, FontWeight = FontWeights.Bold, KeepWithNext = true });
                foreach (var line in group)
                {
                    var details = $"Match {line.MatchNumber} · {line.Stage}  {line.Matchup}\n{line.Fixture.ScheduledAt:dd MMM yyyy} · {line.Fixture.ScheduledAt:h:mm tt}  |  {line.Ground}  |  {line.StatusLabel}";
                    if (line.QualificationLabel.Length > 0) details += "\n" + line.QualificationLabel;
                    if (line.ResultLabel.Length > 0) details += "\n" + line.ResultLabel;
                    if (warnings[line.Fixture.Id].Length > 0) details += "\nWarning: " + warnings[line.Fixture.Id];
                    document.Blocks.Add(new Paragraph(new Run(details)) { KeepTogether = true, Margin = new Thickness(0, 0, 0, 12) });
                }
            }
            print.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, TournamentName + " Fixtures");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not print schedule", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static Window Dialog(string title, double width, double height) => new()
    {
        Title = title, Width = width, Height = height, MinWidth = 440, MinHeight = 520,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow,
        FontFamily = new("Segoe UI"), FontSize = 14, Background = (System.Windows.Media.Brush)Application.Current.Resources["Surface"],
        Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Ink"]
    };

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
    private static TextBlock Label(string text) => new() { Text = text, Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 12, 0, 5), TextWrapping = TextWrapping.Wrap };

    private static (StackPanel Panel, Button Confirm) Footer(Window dialog, string confirmText)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.Close();
        var confirm = new Button { Content = confirmText, Padding = new Thickness(14, 7, 14, 7), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 121, 82)) };
        confirm.Foreground = System.Windows.Media.Brushes.White;
        panel.Children.Add(cancel); panel.Children.Add(confirm);
        return (panel, confirm);
    }

    private static string SafeFileName(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void GoHome(object sender, RoutedEventArgs e) => HomeRequested?.Invoke(this, EventArgs.Empty);
}
