using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CricketAuction.App.ViewModels;
using CricketAuction.Domain;
using CricketAuction.Infrastructure;
using Microsoft.Win32;

namespace CricketAuction.App;
public sealed class DialogService
{
    private Window Owner => Application.Current.MainWindow;
    public void Error(string message) => MessageBox.Show(Owner, message, "Unable to complete operation", MessageBoxButton.OK, MessageBoxImage.Warning);
    public void PhotoFailures(IReadOnlyList<PhotoFailure> failures)
    {
        var window = Shell("Photo download failures", 850, 580); var panel = Form(window);
        panel.Children.Add(Label("Fix the link in Edit, make Drive sharing public, or select a local picture with Photo. Pictures are converted automatically. Cache photos retries missing photos. This list shows the last download attempt."));
        var grid = new DataGrid { ItemsSource = failures, AutoGenerateColumns = false, IsReadOnly = true, Height = 330 };
        grid.Columns.Add(new DataGridTextColumn { Header = "Player ID", Binding = new Binding("PlayerId"), Width = 75 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Player", Binding = new Binding("Name"), Width = 180 });
        var wrap = new Style(typeof(TextBlock)); wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        grid.Columns.Add(new DataGridTextColumn { Header = "Reason / next step", Binding = new Binding("Reason"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), ElementStyle = wrap });
        panel.Children.Add(grid); panel.Children.Add(Button("Close", () => window.Close())); window.ShowDialog();
    }
    private sealed record PresentationRow(int Slide, Player Player);

    public List<Player>? ArrangePresentationPlayers(IReadOnlyList<Player> players, string selection)
    {
        var ordered = players.ToList();
        var window = Shell("Arrange PowerPoint players", 1000, 700);
        var dock = new DockPanel { Margin = new Thickness(24) };
        window.Content = dock;
        var heading = Label($"{selection} · {players.Count} player slides\nSelect a player and move them into the desired slide order. Press Enter to continue to save.");
        DockPanel.SetDock(heading, Dock.Top); dock.Children.Add(heading);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(controls, Dock.Top); dock.Children.Add(controls);
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserSortColumns = false, CanUserReorderColumns = false, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Slide", Binding = new Binding("Slide"), Width = 65 });
        var photoBinding = new Binding("Player.Photo") { Converter = new PhotoConverter() };
        var photoCell = new FrameworkElementFactory(typeof(Grid));
        photoCell.SetValue(FrameworkElement.HeightProperty, 76.0);
        photoCell.SetValue(FrameworkElement.MarginProperty, new Thickness(4));
        var photo = new FrameworkElementFactory(typeof(Image));
        photo.SetValue(Image.StretchProperty, Stretch.Uniform);
        photo.SetBinding(Image.SourceProperty, photoBinding);
        photoCell.AppendChild(photo);
        var placeholder = new FrameworkElementFactory(typeof(TextBlock));
        placeholder.SetValue(TextBlock.TextProperty, "No photo");
        placeholder.SetValue(TextBlock.FontSizeProperty, 11.0);
        placeholder.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        placeholder.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var placeholderStyle = new Style(typeof(TextBlock));
        placeholderStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
        var missingPhoto = new DataTrigger { Binding = photoBinding, Value = null };
        missingPhoto.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible));
        placeholderStyle.Triggers.Add(missingPhoto);
        placeholder.SetValue(FrameworkElement.StyleProperty, placeholderStyle);
        photoCell.AppendChild(placeholder);
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Photo", Width = 90, CellTemplate = new DataTemplate { VisualTree = photoCell } });
        grid.Columns.Add(new DataGridTextColumn { Header = "Player", Binding = new Binding("Player.FullName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Role", Binding = new Binding("Player.PlayerType"), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Player.Status"), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Team", Binding = new Binding("Player.TeamName"), Width = 170 });
        void RefreshOrder(int index)
        {
            grid.ItemsSource = ordered.Select((player, position) => new PresentationRow(position + 1, player)).ToList();
            grid.SelectedIndex = index;
            if (grid.SelectedItem != null) grid.ScrollIntoView(grid.SelectedItem);
        }
        void MoveTo(int destination)
        {
            var index = grid.SelectedIndex;
            if (index < 0 || destination < 0 || destination >= ordered.Count || index == destination) return;
            var player = ordered[index]; ordered.RemoveAt(index); ordered.Insert(destination, player);
            RefreshOrder(destination);
        }
        var top = Button("Move to first", () => MoveTo(0));
        var up = Button("Move up", () => MoveTo(grid.SelectedIndex - 1));
        var down = Button("Move down", () => MoveTo(grid.SelectedIndex + 1));
        var bottom = Button("Move to last", () => MoveTo(ordered.Count - 1));
        foreach (var button in new[] { top, up, down, bottom }) controls.Children.Add(button);
        controls.Children.Add(Button("Reset order", () => { ordered = players.ToList(); RefreshOrder(0); }));
        grid.SelectionChanged += (_, _) =>
        {
            top.IsEnabled = up.IsEnabled = grid.SelectedIndex > 0;
            bottom.IsEnabled = down.IsEnabled = grid.SelectedIndex >= 0 && grid.SelectedIndex < ordered.Count - 1;
        };
        footer.Children.Add(Button("Cancel", () => window.Close()));
        var generate = Button("Generate PowerPoint…", () => window.DialogResult = true);
        generate.IsDefault = true; generate.IsEnabled = ordered.Count > 0; footer.Children.Add(generate);
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && ordered.Count > 0) { e.Handled = true; window.DialogResult = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Up or Key.Down)
            {
                e.Handled = true; MoveTo(grid.SelectedIndex + (e.Key == Key.Up ? -1 : 1));
            }
        };
        dock.Children.Add(grid); RefreshOrder(ordered.Count > 0 ? 0 : -1);
        window.Loaded += (_, _) => grid.Focus();
        return window.ShowDialog() == true ? ordered : null;
    }

    public bool Confirm(string message) => MessageBox.Show(Owner, message, "Confirm auction action", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
    public TeamPosterDesign? ChoosePosterDesign(TeamPosterImageService renderer, Team team, IReadOnlyList<Player> players, AuctionSession session)
    {
        var window = Shell("Choose team poster design", 1080, 780);
        var dock = new DockPanel { Margin = new Thickness(24) };
        window.Content = dock;
        var heading = Label($"{team.Name} — {session.TournamentName}\nChoose a design, then save your poster as a PNG image.");
        DockPanel.SetDock(heading, Dock.Top); dock.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        var designs = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        designs.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        ScrollViewer.SetHorizontalScrollBarVisibility(designs, ScrollBarVisibility.Disabled);
        foreach (var design in Enum.GetValues<TeamPosterDesign>())
        {
            var card = new StackPanel { Width = 290, Margin = new Thickness(8) };
            card.Children.Add(new TextBlock { Text = TeamPosterImageService.DesignName(design), FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
            card.Children.Add(new Image { Source = renderer.Render(team, players, session.TournamentName, design, session.AuctionName, 400), Height = 280, Stretch = Stretch.Uniform });
            designs.Items.Add(new ListBoxItem { Content = card, Tag = design });
        }
        designs.SelectedIndex = 0;
        buttons.Children.Add(Button("Cancel", () => window.Close()));
        var save = Button("Save selected design…", () => window.DialogResult = true);
        save.IsDefault = true; buttons.Children.Add(save);
        dock.Children.Add(designs);
        return window.ShowDialog() == true && designs.SelectedItem is ListBoxItem selected ? (TeamPosterDesign)selected.Tag : null;
    }
    public string? OpenFile(string filter) { var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true }; return dialog.ShowDialog(Owner) == true ? dialog.FileName : null; }
    public string? SaveFile(string filter, string name, string directory)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = name, AddExtension = true, OverwritePrompt = true };
        if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
    private Window Shell(string title, double width = 530, double height = 650)
    {
        var window = new Window { Title = title, Owner = Owner, Width = width, Height = height, MinWidth = Math.Min(width, 440), MinHeight = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
        window.SetResourceReference(Window.BackgroundProperty, "Surface"); window.SetResourceReference(Window.ForegroundProperty, "Ink");
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        return window;
    }
    private static TextBlock Label(string text) => new() { Text = text, Margin = new(0, 12, 0, 5), TextWrapping = TextWrapping.Wrap };
    private static TextBox Field(StackPanel panel, string label, string value = "")
    {
        panel.Children.Add(Label(label)); var field = new TextBox { Text = value, MinWidth = 160 }; panel.Children.Add(field); return field;
    }
    private static Button Button(string text, Action action) { var b = new Button { Content = text, Margin = new(0, 10, 8, 0) }; b.Click += (_, _) => action(); return b; }
    private static StackPanel Form(Window window)
    {
        var panel = new StackPanel { Margin = new(26) }; window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; return panel;
    }
    private void SaveButton(StackPanel panel, Window window, string title, Action save)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Button("Cancel", () => window.Close())); buttons.Children.Add(Button(title, () => { try { save(); window.DialogResult = true; } catch (Exception ex) { Error(ex.Message); } })); panel.Children.Add(buttons);
    }
    public (string Tournament, string Auction, IReadOnlyList<string> Teams, long BudgetPaise)? NewAuction()
    {
        var window = Shell("Welcome to Cricket Auction", width: 600, height: 750); var panel = Form(window);
        panel.Children.Add(new TextBlock { Text = "Create a new auction", FontSize = 27, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Label("Everything is saved on this computer. No account required."));
        var tournament = Field(panel, "Tournament name"); var auction = Field(panel, "Auction name");
        var budget = Field(panel, "Team bidding budget · ₹ per team *");
        panel.Children.Add(Label("How many teams?"));
        var count = new ComboBox { ItemsSource = Enumerable.Range(1, 64).ToArray(), SelectedItem = 4 };
        panel.Children.Add(count);
        panel.Children.Add(Label("Each team gets this purse. A team cannot buy players beyond its budget."));
        var teamPanel = new StackPanel();
        panel.Children.Add(new ScrollViewer { Content = teamPanel, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var teamFields = new List<TextBox>();
        void ShowTeamFields()
        {
            if (count.SelectedItem is not int number) return;
            while (teamFields.Count < number) teamFields.Add(new TextBox { MinWidth = 160 });
            teamPanel.Children.Clear();
            for (var i = 0; i < number; i++) { teamPanel.Children.Add(Label($"Team {i + 1} name")); teamPanel.Children.Add(teamFields[i]); }
        }
        count.SelectionChanged += (_, _) => ShowTeamFields(); ShowTeamFields();
        IReadOnlyList<string> names = Array.Empty<string>();
        long budgetPaise = 0;
        SaveButton(panel, window, "Create auction & teams", () =>
        {
            if (string.IsNullOrWhiteSpace(tournament.Text) || string.IsNullOrWhiteSpace(auction.Text)) throw new InvalidOperationException("Tournament and auction names are required.");
            budgetPaise = Money.Parse(budget.Text);
            if (budgetPaise <= 0) throw new InvalidOperationException("Enter a team budget greater than ₹0.");
            names = teamFields.Take((int)count.SelectedItem).Select(field => field.Text.Trim()).ToArray();
            if (names.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Enter a name for every team.");
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count) throw new InvalidOperationException("Each team must have a different name.");
        });
        window.Loaded += (_, _) => tournament.Focus();
        return window.ShowDialog() == true ? (tournament.Text.Trim(), auction.Text.Trim(), names, budgetPaise) : null;
    }
    public AuctionSummary? SelectAuction(Workspace workspace)
    {
        var auctions = workspace.Auctions();
        var window = Shell("Saved auctions", 900, 560);
        var dock = new DockPanel { Margin = new(22) };
        window.Content = dock;
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Saved auctions", FontSize = 27, FontWeight = FontWeights.SemiBold });
        top.Children.Add(Label("Open an auction, edit its names, or permanently delete it."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        var grid = new DataGrid { ItemsSource = auctions, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(new DataGridTextColumn { Header = "Tournament", Binding = new Binding(nameof(AuctionSummary.TournamentName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Auction", Binding = new Binding(nameof(AuctionSummary.AuctionName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Round", Binding = new Binding(nameof(AuctionSummary.CurrentRound)), Width = 75 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Players", Binding = new Binding(nameof(AuctionSummary.PlayerCount)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Database", Binding = new Binding(nameof(AuctionSummary.DatabasePath)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        dock.Children.Add(grid);
        TextBlock? emptyMessage = null;
        if (auctions.Count == 0) { emptyMessage = Label("No saved auctions were found in the app's auction folder."); top.Children.Add(emptyMessage); }
        void Reload(string? selectedPath = null)
        {
            var refreshed = workspace.Auctions(); grid.ItemsSource = refreshed;
            grid.SelectedItem = selectedPath == null ? null : refreshed.FirstOrDefault(item => item.DatabasePath == selectedPath);
            if (refreshed.Count == 0 && emptyMessage == null) { emptyMessage = Label("No saved auctions were found in the app's auction folder."); top.Children.Add(emptyMessage); }
            else if (refreshed.Count > 0 && emptyMessage != null) { top.Children.Remove(emptyMessage); emptyMessage = null; }
        }
        buttons.Children.Add(Button("Cancel", () => window.Close()));
        buttons.Children.Add(Button("Edit names", () =>
        {
            if (grid.SelectedItem is not AuctionSummary selected) { Error("Select an auction to edit."); return; }
            try { if (EditAuctionNames(workspace, selected)) Reload(selected.DatabasePath); }
            catch (Exception ex) { Error(ex.Message); }
        }));
        buttons.Children.Add(Button("Delete auction", () =>
        {
            if (grid.SelectedItem is not AuctionSummary selected) { Error("Select an auction to delete."); return; }
            if (!Confirm($"Permanently delete this auction and all its players?\n\nTournament: {selected.TournamentName}\nAuction: {selected.AuctionName}\n\nThis cannot be undone. Back up first if you may need it.")) return;
            try { workspace.DeleteAuction(selected.DatabasePath); Reload(); }
            catch (Exception ex) { Error(ex.Message); }
        }));
        buttons.Children.Add(Button("Open auction", () => { if (grid.SelectedItem is AuctionSummary) window.DialogResult = true; else Error("Select an auction to open."); }));
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is AuctionSummary) window.DialogResult = true; };
        return window.ShowDialog() == true ? grid.SelectedItem as AuctionSummary : null;
    }
    public bool EditAuctionNames(Workspace workspace, AuctionSummary auction)
    {
        var window = Shell("Edit auction and team names", 600, 700); var panel = Form(window);
        panel.Children.Add(new TextBlock { Text = "Edit saved auction", FontSize = 25, FontWeight = FontWeights.SemiBold });
        var tournament = Field(panel, "Tournament name", auction.TournamentName); var name = Field(panel, "Auction name", auction.AuctionName);
        panel.Children.Add(Label("Team names"));
        var teamFields = workspace.SavedAuctionTeams(auction.DatabasePath)
            .ToDictionary(team => team.Id, team => Field(panel, $"Team {team.Id}", team.Name));
        if (teamFields.Count == 0) panel.Children.Add(Label("No teams yet. Add teams using the Teams button."));
        SaveButton(panel, window, "Save names", () =>
        {
            if (string.IsNullOrWhiteSpace(tournament.Text) || string.IsNullOrWhiteSpace(name.Text)) throw new InvalidOperationException("Tournament and auction names are required.");
            if (tournament.Text.Trim().Length > 120 || name.Text.Trim().Length > 120) throw new InvalidOperationException("Names must be 120 characters or fewer.");
            workspace.UpdateAuctionNames(auction.DatabasePath, tournament.Text, name.Text,
                teamFields.ToDictionary(pair => pair.Key, pair => pair.Value.Text));
        });
        return window.ShowDialog() == true;
    }
    public string? Choose(string title, string[] choices)
    {
        if (choices.Length == 0) throw new InvalidOperationException("No options are available yet.");
        var window = Shell(title, height: 390); var panel = Form(window); panel.Children.Add(new TextBlock { Text = title, FontSize = 24 });
        var list = new ListBox { ItemsSource = choices, SelectedIndex = 0, Margin = new(0, 20, 0, 10) }; panel.Children.Add(list);
        SaveButton(panel, window, "Continue", () => { }); return window.ShowDialog() == true ? list.SelectedItem as string : null;
    }
    public (Team Team, long Paise, string Notes)? Sale(Player player, List<Team> teams)
    {
        if (teams.Count == 0) throw new InvalidOperationException("Add a team before selling a player.");
        var window = Shell("Sell player", height: 570); var panel = Form(window);
        panel.Children.Add(new TextBlock { Text = player.FullName, FontSize = 28, TextWrapping = TextWrapping.Wrap });
        var amount = Field(panel, "Sold amount · ₹"); panel.Children.Add(Label("Sold to"));
        var team = new ComboBox { ItemsSource = teams, SelectedIndex = 0 }; panel.Children.Add(team);
        var budget = Label(""); panel.Children.Add(budget);
        void Budget() { if (team.SelectedItem is Team t) budget.Text = $"Spent: {t.Spent}     Remaining: {t.Remaining}"; }
        team.SelectionChanged += (_, _) => Budget(); Budget(); var notes = Field(panel, "Notes (optional)");
        long paise = 0; SaveButton(panel, window, "Review sale", () => { paise = Money.Parse(amount.Text); if (paise == 0) throw new InvalidOperationException("Sold amount must be greater than zero."); if (team.SelectedItem == null) throw new InvalidOperationException("Select a team."); });
        window.Loaded += (_, _) => amount.Focus(); return window.ShowDialog() == true ? ((Team)team.SelectedItem, paise, notes.Text) : null;
    }
    public bool ImportPreview(List<ImportRow> rows)
    {
        var window = Shell("Preview player import", 1120, 700); var dock = new DockPanel { Margin = new(22) }; window.Content = dock;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Review registrations", FontSize = 28 });
        top.Children.Add(Label($"{rows.Count(r => r.Error.Length == 0)} valid rows  •  {rows.Count(r => r.IsDuplicate)} possible duplicates  •  {rows.Count(r => r.Error.Length > 0)} invalid rows (skipped)"));
        top.Children.Add(Label("Duplicate decisions: KeepExisting skips the row; UpdateExisting changes registration details only; ImportAsNew deliberately creates another player. Original imported registration and auction history are preserved."));
        top.Children.Add(Label("Different names sharing a phone number import normally. Only matching name AND phone are flagged. Review automatic jersey corrections in Validation. For distinct players with the same name and phone, choose ImportAsNew."));
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
        void Col(string title, string binding, double width) => grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(binding), Width = width, IsReadOnly = true });
        Col("Row", "RowNumber", 48); Col("Name", "Player.FullName", 160); Col("Phone", "Player.PhoneNumber", 115); Col("Type", "Player.PlayerType", 110); Col("Jersey", "Player.Jersey", 110); Col("Validation", "Result", 220);
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Duplicate decision", ItemsSource = Enum.GetValues<DuplicateResolution>(), SelectedItemBinding = new Binding("Resolution") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 160 });
        bottom.Children.Add(Button("Cancel", () => window.Close())); bottom.Children.Add(Button("Confirm import", () => { grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true); window.DialogResult = true; }));
        dock.Children.Add(grid); return window.ShowDialog() == true;
    }
    public List<Player>? SelectPlayers(List<Player> players)
    {
        var window = Shell("Select unsold players", 650, 650); var panel = Form(window); panel.Children.Add(Label("Select multiple players using Ctrl or Shift."));
        var list = new ListBox { ItemsSource = players, DisplayMemberPath = "FullName", SelectionMode = SelectionMode.Extended, Height = 440 }; panel.Children.Add(list);
        SaveButton(panel, window, "Generate slides", () => { if (list.SelectedItems.Count == 0) throw new InvalidOperationException("Select at least one player."); });
        return window.ShowDialog() == true ? list.SelectedItems.Cast<Player>().ToList() : null;
    }
    public Player? EditPlayer(Player original)
    {
        var window = Shell(original.Id == 0 ? "Add player" : "Edit player", 600, 780); var panel = Form(window);
        var fields = new Dictionary<string, TextBox>();
        foreach (var (property, label) in new[] { ("FullName", "Full name *"), ("Age", "Age"), ("PhoneNumber", "Phone number *"), ("PlayerType", "Player type"), ("BattingStyle", "Batting style"), ("BowlingStyle", "Bowling style"), ("AreaCity", "Area / City"), ("JerseyName", "Jersey name"), ("JerseyNumber", "Jersey number"), ("JerseySize", "Jersey size"), ("PlayerPhotoUrl", "Public photo URL") })
            fields[property] = Field(panel, label, typeof(Player).GetProperty(property)!.GetValue(original)?.ToString() ?? "");
        Player? result = null;
        SaveButton(panel, window, "Save player", () =>
        {
            result = new Player { Id = original.Id };
            foreach (var f in fields)
            {
                if (f.Key == "Age") { if (f.Value.Text.Length > 0) { if (!int.TryParse(f.Value.Text, out var age)) throw new InvalidOperationException("Age must be a whole number."); result.Age = age; } }
                else typeof(Player).GetProperty(f.Key)!.SetValue(result, f.Value.Text.Trim());
            }
            if (PlayerRules.Validate(result) is { } error) throw new InvalidOperationException(error);
        });
        return window.ShowDialog() == true ? result : null;
    }
    public void Teams(AuctionService auction, AuctionSession session)
    {
        var window = Shell("Teams & squads", 960, 720); var panel = Form(window);
        panel.Children.Add(Label("Compare remaining budgets below. NEAR LIMIT means 80% or more spent; LIMIT REACHED means no budget remains. " + (session.EnableBudgets ? "Budget limits are enforced." : "Budget enforcement is disabled.")));
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 230, SelectionMode = DataGridSelectionMode.Single };
        foreach (var (header, property) in new[] { ("Team", "Name"), ("Owner name", "OwnerName"), ("Budget", "Budget"), ("Spent", "Spent"), ("Remaining", "Remaining"), ("Players", "PlayerCount"), ("Budget alert", "BudgetWarning") }) grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var alertColumn = (DataGridTextColumn)grid.Columns[^1];
        alertColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(169, 102, 24))), new Setter(TextBlock.FontWeightProperty, FontWeights.Bold) } };
        panel.Children.Add(grid); var squad = Label(""); panel.Children.Add(squad);
        var name = Field(panel, "Team name"); var owner = Field(panel, "Owner name"); var budget = Field(panel, "Budget · ₹ (blank means unlimited)", session.DefaultBudgetPaise.HasValue ? (session.DefaultBudgetPaise.Value / 100m).ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
        int editingId = 0; byte[]? logo = null;
        void Reload() { grid.ItemsSource = auction.Teams(); }
        grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is Team t) { editingId = t.Id; name.Text = t.Name; owner.Text = t.OwnerName; budget.Text = t.BudgetPaise.HasValue ? (t.BudgetPaise.Value / 100m).ToString(System.Globalization.CultureInfo.InvariantCulture) : ""; logo = t.Logo; squad.Text = "Squad: " + string.Join(" • ", auction.Players().Where(p => p.TeamId == t.Id).Select(p => $"{p.FullName} ({p.SoldPrice})")); } };
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        buttons.Children.Add(Button("New team", () => { grid.SelectedItem = null; editingId = 0; name.Text = owner.Text = ""; budget.Text = session.DefaultBudgetPaise.HasValue ? (session.DefaultBudgetPaise.Value / 100m).ToString(System.Globalization.CultureInfo.InvariantCulture) : ""; logo = null; }));
        buttons.Children.Add(Button("Choose logo", () => { try { var file = OpenFile("Image|*.png;*.jpg;*.jpeg"); if (file != null) logo = LoadPhoto(file); } catch (Exception ex) { Error(ex.Message); } }));
        buttons.Children.Add(Button("Save team", () => { try { auction.SaveTeam(new() { Id = editingId, Name = name.Text, OwnerName = owner.Text, BudgetPaise = string.IsNullOrWhiteSpace(budget.Text) ? null : Money.Parse(budget.Text), Logo = logo }); Reload(); editingId = 0; name.Text = owner.Text = ""; logo = null; } catch (Exception ex) { Error(ex.Message); } }));
        buttons.Children.Add(Button("Done", () => window.Close())); Reload(); window.ShowDialog();
    }
    public AuctionSession? Settings(AuctionSession original)
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AuctionSession>(System.Text.Json.JsonSerializer.Serialize(original))!;
        var window = Shell("Auction settings", 650, 780); var panel = Form(window);
        var tournament = Field(panel, "Tournament name", settings.TournamentName); var auction = Field(panel, "Auction name", settings.AuctionName);
        var round = Field(panel, "Starting round (editable before importing players)", settings.CurrentRound.ToString());
        var enabled = new CheckBox { Content = "Enforce team budgets", IsChecked = settings.EnableBudgets, Margin = new(0, 20, 0, 5) }; panel.Children.Add(enabled);
        var budget = Field(panel, "Default team budget · ₹ (optional)", settings.DefaultBudgetPaise.HasValue ? (settings.DefaultBudgetPaise.Value / 100m).ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
        panel.Children.Add(Label("Currency: INR · Indian rupees (₹)"));
        panel.Children.Add(Label("Photos are stored inside the auction database. An optional cache folder below also receives copies for easy access."));
        var cache = Field(panel, "Photo cache folder (optional)", settings.PhotoCacheLocation); var export = Field(panel, "Default Excel folder", settings.ExportLocation); var ppt = Field(panel, "Default PowerPoint folder", settings.PptLocation);
        panel.Children.Add(Label("Theme")); var theme = new ComboBox { ItemsSource = new[] { "System", "Light", "Dark" }, SelectedItem = settings.Theme }; panel.Children.Add(theme);
        SaveButton(panel, window, "Save settings", () =>
        {
            if (!int.TryParse(round.Text, out var value) || value < 1) throw new InvalidOperationException("Round must be a positive whole number.");
            settings.TournamentName = tournament.Text.Trim(); settings.AuctionName = auction.Text.Trim(); settings.CurrentRound = value; settings.EnableBudgets = enabled.IsChecked == true;
            settings.DefaultBudgetPaise = string.IsNullOrWhiteSpace(budget.Text) ? null : Money.Parse(budget.Text);
            settings.PhotoCacheLocation = cache.Text.Trim(); settings.ExportLocation = export.Text.Trim(); settings.PptLocation = ppt.Text.Trim(); settings.Theme = theme.SelectedItem?.ToString() ?? "System";
        });
        return window.ShowDialog() == true ? settings : null;
    }
    public byte[] LoadPhoto(string path)
    {
        return PhotoNormalizer.FromFile(path);
    }
    public byte[] NormalizePhoto(byte[] bytes)
    {
        return PhotoNormalizer.FromBytes(bytes);
    }
    public byte[] RotatePhoto(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        var rotated = new TransformedBitmap(image, new RotateTransform(90)); rotated.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rotated)); using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    public void ApplyTheme(string theme)
    {
        var dark = theme == "Dark";
        if (theme == "System") { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0; }
        var resources = Application.Current.Resources;
        foreach (var (name, value) in new[] { ("Canvas", dark ? "#10211E" : "#F3F6F3"), ("Surface", dark ? "#19312B" : "#FFFFFF"), ("Ink", dark ? "#F1F6F2" : "#16372E"), ("Muted", dark ? "#ABC2B8" : "#63786F"), ("Line", dark ? "#355047" : "#DCE6DF"), ("Input", dark ? "#244238" : "#F7FAF8") }) resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }
    private Window? projector;
    public void Projector(MainViewModel vm)
    {
        if (projector != null) { projector.Activate(); return; }
        projector = new Views.ProjectorWindow { DataContext = vm }; projector.Closed += (_, _) => projector = null; projector.Show();
    }
}
