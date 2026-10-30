using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CricketAuction.Domain;
using CricketAuction.Infrastructure;

namespace CricketAuction.App;

public partial class TeamsSquadsWindow : Window, INotifyPropertyChanged
{
    private readonly AuctionService auction;
    private readonly Workspace workspace;
    private Team? selectedTeam;
    private string rosterSearch = "";
    private List<Player> soldPlayers = [];
    private List<Player> selectedSquad = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? HomeRequested;
    public ObservableCollection<Team> Teams { get; } = [];
    public ObservableCollection<Player> Roster { get; } = [];
    public string TournamentName => workspace.DatabasePath.Length == 0 ? "No active tournament" : workspace.Session().TournamentName;
    public Team? SelectedTeam
    {
        get => selectedTeam;
        set
        {
            if (ReferenceEquals(selectedTeam, value)) return;
            selectedTeam = value;
            Changed();
            Changed(nameof(CaptainText));
            Changed(nameof(SpendText));
            Changed(nameof(HasBudget));
            Changed(nameof(CompositionText));
            RefreshRoster();
        }
    }
    public string RosterSearch
    {
        get => rosterSearch;
        set
        {
            if (rosterSearch == value) return;
            rosterSearch = value;
            Changed();
            RefreshRoster();
        }
    }
    public bool HasBudget => SelectedTeam?.BudgetPaise.HasValue == true;
    public string CaptainText => SelectedTeam == null ? "" : $"Captain / owner: {Fallback(SelectedTeam.OwnerName, "Not set")}";
    public string SpendText => SelectedTeam == null ? "" : $"Spent {SelectedTeam.Spent}  ·  Budget {SelectedTeam.Budget}";
    public string RosterCountText => $"{Roster.Count} players";
    public string CompositionText
    {
        get
        {
            if (SelectedTeam == null) return "Choose a team to view its squad.";
            var members = Members(SelectedTeam);
            if (members.Count == 0) return "No players purchased yet.";
            var roles = members.GroupBy(player => NormalizeRole(player.PlayerType)).OrderBy(group => group.Key)
                .Select(group => $"{group.Key} {group.Count()}");
            return $"Squad composition  ·  {string.Join("   ·   ", roles)}";
        }
    }

    public TeamsSquadsWindow(AuctionService auction, Workspace workspace)
    {
        InitializeComponent();
        this.auction = auction;
        this.workspace = workspace;
        DataContext = this;
        Activated += (_, _) => Reload();
        Reload();
    }

    private void Reload()
    {
        if (workspace.DatabasePath.Length == 0) return;
        var selectedId = SelectedTeam?.Id;
        var teams = auction.Teams();
        soldPlayers = auction.Players().Where(player => player.Status == AuctionStatus.Sold).ToList();
        Teams.Clear();
        foreach (var team in teams) Teams.Add(team);
        Changed(nameof(TournamentName));
        SelectedTeam = Teams.FirstOrDefault(team => team.Id == selectedId) ?? Teams.FirstOrDefault();
    }

    private List<Player> Members(Team team) => soldPlayers.Where(player => player.TeamId == team.Id)
        .OrderBy(player => player.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void RefreshRoster()
    {
        selectedSquad = SelectedTeam == null ? [] : Members(SelectedTeam);
        var term = RosterSearch.Trim();
        var visiblePlayers = term.Length == 0 ? selectedSquad : selectedSquad.Where(player =>
            player.FullName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || player.PlayerType.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || player.Jersey.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || player.BattingStyle.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || player.BowlingStyle.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        Roster.Clear();
        foreach (var player in visiblePlayers) Roster.Add(player);
        Changed(nameof(RosterCountText));
        Changed(nameof(CompositionText));
    }

    private static string NormalizeRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role)) return "Unspecified";
        var value = role.Trim();
        return value.Contains("all-round", StringComparison.OrdinalIgnoreCase) || value.Contains("allround", StringComparison.OrdinalIgnoreCase)
            ? "All-rounder" : value;
    }

    private static string Fallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void GoHome(object sender, RoutedEventArgs e) => HomeRequested?.Invoke(this, EventArgs.Empty);
}
