using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CricketAuction.Domain;
using CricketAuction.Infrastructure;

namespace CricketAuction.App;

public partial class TeamsSquadsWindow : Window, INotifyPropertyChanged
{
    private readonly AuctionService auction;
    private readonly Workspace workspace;
    private Team? selectedTeam;
    private string rosterSearch = "";
    private List<Player> squadPlayers = [];
    private List<Player> selectedSquad = [];
    private bool canAssignIcons;

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
            Changed(nameof(HasIconPlayer));
            Changed(nameof(CanAssignIconPlayer));
            Changed(nameof(CanRemoveIconPlayer));
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
    public bool HasIconPlayer => SelectedTeam?.IconPlayerId.HasValue == true;
    public bool CanAssignIconPlayer => canAssignIcons && SelectedTeam != null && SelectedTeam.PlayerCount < Team.SquadLimit;
    public bool CanRemoveIconPlayer => canAssignIcons && HasIconPlayer;
    public string CaptainText => SelectedTeam == null ? "" : $"Captain / owner: {Fallback(SelectedTeam.OwnerName, "Not set")}";
    public string SpendText => SelectedTeam == null ? "" : $"Spent {SelectedTeam.Spent}  ·  Budget {SelectedTeam.Budget}";
    public string RosterCountText => SelectedTeam == null ? "0 squad players" : $"{selectedSquad.Count} / {Team.SquadLimit} squad places";
    public string CompositionText
    {
        get
        {
            if (SelectedTeam == null) return "Choose a team to view its squad.";
            var members = Members(SelectedTeam);
            if (members.Count == 0) return "No players assigned yet.";
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
        squadPlayers = auction.Players().Where(player => player.Status == AuctionStatus.Sold || player.IsIconPlayer).ToList();
        canAssignIcons = auction.CanAssignIconPlayers();
        Teams.Clear();
        foreach (var team in teams) Teams.Add(team);
        Changed(nameof(TournamentName));
        SelectedTeam = Teams.FirstOrDefault(team => team.Id == selectedId) ?? Teams.FirstOrDefault();
    }

    private List<Player> Members(Team team) => squadPlayers.Where(player => (player.Status == AuctionStatus.Sold && player.TeamId == team.Id)
            || (player.IsIconPlayer && player.IconTeamId == team.Id))
        .OrderByDescending(player => player.IsIconPlayer)
        .ThenBy(player => player.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void RefreshRoster()
    {
        selectedSquad = SelectedTeam == null ? [] : Members(SelectedTeam);
        var term = RosterSearch.Trim();
        var visiblePlayers = term.Length == 0 ? selectedSquad : selectedSquad.Where(player =>
            player.FullName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || player.PlayerType.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || player.Jersey.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || player.BattingStyle.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || player.BowlingStyle.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || player.IconTeamName.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        Roster.Clear();
        foreach (var player in visiblePlayers) Roster.Add(player);
        Changed(nameof(RosterCountText));
        Changed(nameof(CompositionText));
    }

    private void AssignIconPlayer(object sender, RoutedEventArgs e)
    {
        if (SelectedTeam == null || !CanAssignIconPlayer) return;
        var available = auction.Players().Where(player => player.Status == AuctionStatus.Available && !player.IsIconPlayer)
            .OrderBy(player => player.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (available.Count == 0)
        {
            MessageBox.Show(this, "There are no available players to assign as an icon player.", "Assign icon player", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Window
        {
            Title = $"Assign icon player · {SelectedTeam.Name}", Width = 720, Height = 760, MinWidth = 620, MinHeight = 620,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily, FontSize = FontSize,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["Surface"], Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Ink"]
        };
        var panel = new DockPanel { Margin = new Thickness(22) }; dialog.Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Assign icon player", FontSize = 23, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = $"Choose one available player for {SelectedTeam.Name}. The assignment is free and uses one squad place.", TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 5, 0, 12) });
        var search = new TextBox { MinHeight = 34, Margin = new Thickness(0, 0, 0, 10), ToolTip = "Search player name, role or jersey" };
        System.Windows.Automation.AutomationProperties.SetName(search, "Search available players");
        top.Children.Add(search);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        var availableView = CollectionViewSource.GetDefaultView(available);
        availableView.Filter = item => item is Player player &&
        (
            search.Text.Length == 0
            || player.FullName.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)
            || player.PlayerType.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)
            || player.Jersey.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)
        );
        var list = new ListBox { ItemsSource = availableView, SelectedIndex = 0, BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["Line"] };
        var playerTemplate = new DataTemplate(typeof(Player));
        var playerRow = new FrameworkElementFactory(typeof(DockPanel));
        playerRow.SetValue(FrameworkElement.MinHeightProperty, 76.0);
        var photoFrame = new FrameworkElementFactory(typeof(Border));
        photoFrame.SetValue(FrameworkElement.WidthProperty, 54.0);
        photoFrame.SetValue(FrameworkElement.HeightProperty, 62.0);
        photoFrame.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 10, 5));
        photoFrame.SetValue(Border.BackgroundProperty, Application.Current.Resources["Line"]);
        photoFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        photoFrame.SetValue(DockPanel.DockProperty, Dock.Left);
        var photo = new FrameworkElementFactory(typeof(Image));
        photo.SetValue(Image.StretchProperty, System.Windows.Media.Stretch.UniformToFill);
        photo.SetValue(UIElement.ClipToBoundsProperty, true);
        photo.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Player.Photo)) { Converter = Application.Current.Resources["Photo"] as IValueConverter });
        photoFrame.AppendChild(photo);
        playerRow.AppendChild(photoFrame);
        var details = new FrameworkElementFactory(typeof(StackPanel));
        var playerName = new FrameworkElementFactory(typeof(TextBlock));
        playerName.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Player.FullName)));
        playerName.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        playerName.SetValue(TextBlock.FontSizeProperty, 14.0);
        details.AppendChild(playerName);
        var playerDetails = new FrameworkElementFactory(typeof(TextBlock));
        playerDetails.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Player.PlayerType)) { StringFormat = "{0}" });
        playerDetails.SetValue(TextBlock.ForegroundProperty, Application.Current.Resources["Muted"]);
        playerDetails.SetValue(TextBlock.FontSizeProperty, 12.0);
        details.AppendChild(playerDetails);
        var playerIdentity = new FrameworkElementFactory(typeof(TextBlock));
        playerIdentity.SetBinding(TextBlock.TextProperty, new System.Windows.Data.MultiBinding
        {
            StringFormat = "Jersey {0}   ·   Phone {1}",
            Bindings = { new System.Windows.Data.Binding(nameof(Player.Jersey)), new System.Windows.Data.Binding(nameof(Player.PhoneNumber)) }
        });
        playerIdentity.SetValue(TextBlock.ForegroundProperty, Application.Current.Resources["Muted"]);
        playerIdentity.SetValue(TextBlock.FontSizeProperty, 11.0);
        details.AppendChild(playerIdentity);
        playerRow.AppendChild(details);
        playerTemplate.VisualTree = playerRow;
        list.ItemTemplate = playerTemplate;
        search.TextChanged += (_, _) =>
        {
            availableView.Refresh();
            if (list.SelectedItem == null) list.SelectedIndex = 0;
        };
        panel.Children.Add(list);
        Player? chosen = null;
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var assign = new Button { Content = "Assign free icon player", Padding = new Thickness(14, 7, 14, 7), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 121, 82)), Foreground = System.Windows.Media.Brushes.White };
        assign.Click += (_, _) => { chosen = list.SelectedItem as Player; if (chosen != null) dialog.DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(assign);
        if (dialog.ShowDialog() != true || chosen == null) return;
        if (MessageBox.Show(this, $"Assign {chosen.FullName} to {SelectedTeam.Name} as a free icon player?\n\nThe player will be excluded from bidding and count toward the {Team.SquadLimit}-player squad limit.", "Confirm icon player", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { auction.AssignIconPlayer(chosen.Id, SelectedTeam.Id); Reload(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not assign icon player", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void RemoveIconPlayer(object sender, RoutedEventArgs e)
    {
        if (SelectedTeam == null || !CanRemoveIconPlayer) return;
        var icons = squadPlayers.Where(player => player.IsIconPlayer && player.IconTeamId == SelectedTeam.Id)
            .OrderBy(player => player.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (icons.Count == 0) return;
        var dialog = new Window
        {
            Title = $"Remove icon player · {SelectedTeam.Name}", Width = 560, Height = 520, MinWidth = 480, MinHeight = 420,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily, FontSize = FontSize,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["Surface"], Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Ink"]
        };
        var panel = new DockPanel { Margin = new Thickness(22) }; dialog.Content = panel;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Choose an icon player to remove", FontSize = 21, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "The selected player returns to the available auction list. Other icon players stay assigned.", TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 5, 0, 12) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var list = new ListBox { ItemsSource = icons, DisplayMemberPath = nameof(Player.FullName), SelectedIndex = 0, BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["Line"] };
        panel.Children.Add(list);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        Player? chosen = null;
        var remove = new Button { Content = "Remove selected", Padding = new Thickness(14, 7, 14, 7), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 121, 82)), Foreground = System.Windows.Media.Brushes.White };
        remove.Click += (_, _) => { chosen = list.SelectedItem as Player; if (chosen != null) dialog.DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(remove);
        if (dialog.ShowDialog() != true || chosen == null) return;
        if (MessageBox.Show(this, $"Remove {chosen.FullName} from {SelectedTeam.Name}'s icon players? The player returns to the available auction list.", "Confirm icon removal", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { auction.RemoveIconPlayer(SelectedTeam.Id, chosen.Id); Reload(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not remove icon player", MessageBoxButton.OK, MessageBoxImage.Warning); }
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
