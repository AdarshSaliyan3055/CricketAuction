using System.Windows;
using CricketAuction.App.ViewModels;
using CricketAuction.Domain;
using CricketAuction.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
namespace CricketAuction.App;
public partial class App : Application
{
    private ServiceProvider? services;
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smokeTest = e.Args.Contains("--smoke-test");
        instanceMutex = new Mutex(true, smokeTest ? @"Local\CricketAuctionSmoke_" + Environment.ProcessId : @"Local\CricketAuctionDesktop", out var first);
        if (!first) { MessageBox.Show("Cricket Auction is already running. Switch to its existing window."); Shutdown(); return; }
        var collection = new ServiceCollection(); var rootArg = Array.IndexOf(e.Args, "--data-dir");
        collection.AddSingleton(new Workspace(rootArg >= 0 && rootArg + 1 < e.Args.Length ? e.Args[rootArg + 1] : null));
        collection.AddSingleton<AppLog>(); collection.AddSingleton<AuctionService>();
        collection.AddSingleton<IAuctionService>(s => s.GetRequiredService<AuctionService>());
        collection.AddSingleton<IPlayerService>(s => s.GetRequiredService<AuctionService>());
        collection.AddSingleton<ITeamService>(s => s.GetRequiredService<AuctionService>());
        collection.AddSingleton<IImportService, ImportService>(); collection.AddSingleton<IExportService, ExportService>();
        collection.AddSingleton<IPowerPointService, PowerPointService>(); collection.AddSingleton<IImageService, ImageService>();
        collection.AddSingleton<TeamReportPdfService>();
        collection.AddSingleton<TeamPosterImageService>();
        collection.AddSingleton<FixtureService>();
        collection.AddSingleton<IBackupService, BackupService>(); collection.AddSingleton<DialogService>(); collection.AddSingleton<MainViewModel>();
        services = collection.BuildServiceProvider();
        DispatcherUnhandledException += (_, args) => { services.GetRequiredService<AppLog>().Write("UI error", args.Exception); args.Handled = true; if (e.Args.Contains("--smoke-test")) { System.IO.File.WriteAllText(System.IO.Path.Combine(services.GetRequiredService<Workspace>().Root, "smoke-error.txt"), args.Exception.ToString()); Shutdown(1); } else MessageBox.Show("Unable to complete the operation.\n" + args.Exception.Message, "Cricket Auction", MessageBoxButton.OK, MessageBoxImage.Warning); };
        var viewModel = services.GetRequiredService<MainViewModel>();
        MainWindow = smokeTest
            ? new MainWindow(viewModel)
            : new TournamentHubWindow(viewModel, services.GetRequiredService<AuctionService>(), services.GetRequiredService<Workspace>(), services.GetRequiredService<FixtureService>());
        ShutdownMode = ShutdownMode.OnMainWindowClose; MainWindow.Show();
        if (e.Args.Contains("--smoke-test"))
        {
            SmokeHarness.Prepare(services);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => { timer.Stop(); SmokeHarness.Capture(MainWindow, services); Shutdown(); }; timer.Start();
        }
    }
    protected override void OnExit(ExitEventArgs e) { services?.Dispose(); instanceMutex?.Dispose(); base.OnExit(e); }
}
