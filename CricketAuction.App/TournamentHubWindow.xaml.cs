using System.Windows;
using CricketAuction.App.ViewModels;
using CricketAuction.Infrastructure;

namespace CricketAuction.App;

public partial class TournamentHubWindow : Window
{
    private readonly MainViewModel vm;
    private readonly AuctionService auction;
    private readonly Workspace workspace;
    private readonly FixtureService fixtureService;
    private MainWindow? auctionWindow;
    private TeamsSquadsWindow? teamsWindow;
    private FixturesWindow? fixturesWindow;

    public TournamentHubWindow(MainViewModel viewModel, AuctionService auction, Workspace workspace, FixtureService fixtureService)
    {
        InitializeComponent();
        vm = viewModel;
        this.auction = auction;
        this.workspace = workspace;
        this.fixtureService = fixtureService;
        DataContext = vm;
        Loaded += (_, _) => RefreshTournament();
    }

    private void HubVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) RefreshTournament();
    }

    private void RefreshTournament()
    {
        if (vm.HasSession) vm.Refresh();
    }

    private void OpenAuction(object sender, RoutedEventArgs e)
    {
        Hide();
        if (auctionWindow == null)
        {
            auctionWindow = new MainWindow(vm);
            auctionWindow.HomeRequested += (_, _) => { auctionWindow.Hide(); Show(); Activate(); };
            auctionWindow.Closed += (_, _) => { auctionWindow = null; Show(); Activate(); };
        }
        auctionWindow.Show();
        auctionWindow.Activate();
    }

    private void OpenTeams(object sender, RoutedEventArgs e)
    {
        if (!vm.HasSession)
        {
            MessageBox.Show(this, "Open the Auction feature and create or select a tournament first.", "Teams & Squads", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Hide();
        if (teamsWindow == null)
        {
            teamsWindow = new TeamsSquadsWindow(auction, workspace);
            teamsWindow.HomeRequested += (_, _) => { teamsWindow.Hide(); Show(); Activate(); };
            teamsWindow.Closed += (_, _) => { teamsWindow = null; Show(); Activate(); };
        }
        teamsWindow.Show();
        teamsWindow.Activate();
    }

    private void OpenFixtures(object sender, RoutedEventArgs e)
    {
        if (!vm.HasSession)
        {
            MessageBox.Show(this, "Open the Auction feature and create or select a tournament first.", "Fixtures", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Hide();
        if (fixturesWindow == null)
        {
            fixturesWindow = new FixturesWindow(fixtureService, auction, workspace);
            fixturesWindow.HomeRequested += (_, _) => { fixturesWindow.Hide(); Show(); Activate(); };
            fixturesWindow.Closed += (_, _) => { fixturesWindow = null; Show(); Activate(); };
        }
        fixturesWindow.Show();
        fixturesWindow.Activate();
    }
}