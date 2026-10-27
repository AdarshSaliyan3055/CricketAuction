using CricketAuction.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CricketAuction.Infrastructure;

public sealed class AuctionDb(string path, bool readOnly = false) : DbContext
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<AuctionSession> Sessions => Set<AuctionSession>();
    public DbSet<AuctionHistory> History => Set<AuctionHistory>();
    protected override void OnConfiguring(DbContextOptionsBuilder b) => b.UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 10 }.ToString());
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Player>().HasIndex(p => p.FullName);
        b.Entity<Player>().HasIndex(p => p.PhoneNumber);
        b.Entity<Player>().HasOne(p => p.Team).WithMany().HasForeignKey(p => p.TeamId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AuctionHistory>().HasOne(h => h.Player).WithMany().HasForeignKey(h => h.PlayerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AuctionHistory>().HasIndex(h => h.ActionId);
        b.Entity<Team>().HasIndex(t => t.Name).IsUnique();
        b.Entity<Player>().ToTable(t => { t.HasCheckConstraint("CK_Player_Sale", "(Status = 1 AND SoldPaise > 0 AND TeamId IS NOT NULL) OR (Status <> 1 AND SoldPaise = 0 AND TeamId IS NULL)"); t.HasCheckConstraint("CK_Player_Round", "AuctionRound >= 1"); });
        b.Entity<Team>().ToTable(t => t.HasCheckConstraint("CK_Team_Budget", "BudgetPaise IS NULL OR BudgetPaise >= 0"));
    }
}

public sealed class Workspace
{
    public string Root { get; }
    public string DatabasePath { get; private set; } = "";
    public Workspace(string? root = null)
    {
        Root = root ?? Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(Root);
        var pointer = Path.Combine(Root, "last-auction.txt");
        if (File.Exists(pointer)) { var last = File.ReadAllText(pointer); if (File.Exists(last)) DatabasePath = last; }
    }
    public List<AuctionSummary> Auctions()
    {
        var auctions = new List<AuctionSummary>();
        foreach (var path in Directory.EnumerateFiles(Root, "*.db", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var db = new AuctionDb(path, true);
                var session = db.Sessions.AsNoTracking().Single();
                auctions.Add(new(session.TournamentName, session.AuctionName, session.CurrentRound, db.Players.Count(), path));
            }
            catch (Exception) { }
        }
        return auctions.OrderByDescending(item => File.GetLastWriteTimeUtc(item.DatabasePath)).ToList();
    }
    public AuctionDb Open() => DatabasePath.Length == 0 ? throw new InvalidOperationException("Create or open an auction first.") : new(DatabasePath);
    public void Create(string tournament, string auction, string? path = null, IReadOnlyList<string>? teamNames = null, long? teamBudgetPaise = null)
    {
        if (string.IsNullOrWhiteSpace(tournament) || string.IsNullOrWhiteSpace(auction)) throw new InvalidOperationException("Tournament and auction names are required.");
        var names = teamNames?.Select(name => name.Trim()).ToArray() ?? [];
        if (teamNames != null && (names.Length is < 1 or > 64 || names.Any(string.IsNullOrWhiteSpace))) throw new InvalidOperationException("Enter between 1 and 64 teams, each with a name.");
        if (teamNames != null && (!teamBudgetPaise.HasValue || teamBudgetPaise.Value <= 0)) throw new InvalidOperationException("Enter a positive budget for every team.");
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new InvalidOperationException("Each team must have a different name.");
        path ??= Path.Combine(Root, $"Auction_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..6]}.db");
        if (File.Exists(path)) throw new InvalidOperationException("That file already exists. Choose a new filename.");
        using (var db = new AuctionDb(path))
        {
            db.Database.EnsureCreated();
            using var transaction = db.Database.BeginTransaction();
            db.Sessions.Add(new() { TournamentName = tournament.Trim(), AuctionName = auction.Trim(), DefaultBudgetPaise = teamBudgetPaise, EnableBudgets = true });
            db.Teams.AddRange(names.Select(name => new Team { Name = name, BudgetPaise = teamBudgetPaise }));
            db.SaveChanges();
            db.Database.ExecuteSqlRaw("CREATE TRIGGER history_no_update BEFORE UPDATE ON History BEGIN SELECT RAISE(ABORT, 'Auction history is immutable'); END;");
            db.Database.ExecuteSqlRaw("CREATE TRIGGER history_no_delete BEFORE DELETE ON History BEGIN SELECT RAISE(ABORT, 'Auction history is immutable'); END;");
            transaction.Commit();
        }
        Select(path);
    }
    public static void ValidateDatabase(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("Auction database was not found.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand(); cmd.CommandText = "PRAGMA integrity_check";
        if ((string?)cmd.ExecuteScalar() != "ok") throw new InvalidOperationException("The database failed its integrity check.");
        cmd.CommandText = "SELECT SchemaVersion FROM Sessions WHERE Id=1";
        if (Convert.ToInt32(cmd.ExecuteScalar()) != 1) throw new InvalidOperationException("This auction uses an unsupported database version.");
        using var db = new AuctionDb(path, true);
        _ = db.Players.AsNoTracking().Take(1).ToList(); _ = db.Teams.AsNoTracking().Take(1).ToList(); _ = db.History.AsNoTracking().Take(1).ToList();
    }
    public void Select(string path)
    {
        ValidateDatabase(path);
        DatabasePath = Path.GetFullPath(path);
        File.WriteAllText(Path.Combine(Root, "last-auction.txt"), DatabasePath);
    }
    public List<Team> SavedAuctionTeams(string path)
    {
        using var db = new AuctionDb(ManagedDatabasePath(path), true);
        return db.Teams.AsNoTracking().OrderBy(team => team.Id).ToList();
    }
    public void UpdateAuctionNames(string path, string tournament, string auction, IReadOnlyDictionary<int, string>? teamNames = null)
    {
        if (string.IsNullOrWhiteSpace(tournament) || string.IsNullOrWhiteSpace(auction)) throw new InvalidOperationException("Tournament and auction names are required.");
        if (tournament.Trim().Length > 120 || auction.Trim().Length > 120) throw new InvalidOperationException("Names must be 120 characters or fewer.");
        var database = ManagedDatabasePath(path);
        ValidateDatabase(database);
        using var db = new AuctionDb(database);
        using var transaction = db.Database.BeginTransaction();
        if (teamNames != null)
        {
            var teams = db.Teams.ToList();
            if (teams.Count != teamNames.Count || teams.Any(team => !teamNames.ContainsKey(team.Id)))
                throw new InvalidOperationException("The team list changed. Reopen Edit names and try again.");
            var names = teamNames.ToDictionary(pair => pair.Key, pair => pair.Value.Trim());
            if (names.Values.Any(name => name.Length is < 1 or > 120))
                throw new InvalidOperationException("Each team name must contain 1–120 characters.");
            if (names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                throw new InvalidOperationException("Each team must have a different name.");
            // Temporary unique names permit swapping two names without violating the index.
            foreach (var team in teams) team.Name = "rename-" + Guid.NewGuid().ToString("N");
            db.SaveChanges();
            foreach (var team in teams) team.Name = names[team.Id];
        }
        var session = db.Sessions.Single();
        session.TournamentName = tournament.Trim();
        session.AuctionName = auction.Trim();
        db.SaveChanges();
        transaction.Commit();
    }
    public void DeleteAuction(string path)
    {
        var database = ManagedDatabasePath(path);
        ValidateDatabase(database);
        var wasActive = DatabasePath.Length > 0 && string.Equals(Path.GetFullPath(DatabasePath), database, PathComparison);
        File.Delete(database);
        if (wasActive)
        {
            var replacement = Auctions().FirstOrDefault();
            if (replacement == null)
            {
                DatabasePath = "";
                var pointer = Path.Combine(Root, "last-auction.txt");
                if (File.Exists(pointer)) File.Delete(pointer);
            }
            else Select(replacement.DatabasePath);
        }
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(database + suffix)) File.Delete(database + suffix);
    }
    private string ManagedDatabasePath(string path)
    {
        var database = Path.GetFullPath(path);
        var root = Path.GetFullPath(Root);
        if (!string.Equals(Path.GetDirectoryName(database), root, PathComparison) || !Path.GetExtension(database).Equals(".db", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only saved auctions in the app's auction folder can be changed.");
        if (!File.Exists(database)) throw new InvalidOperationException("Auction database was not found.");
        return database;
    }
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public AuctionSession Session() { using var db = Open(); return db.Sessions.AsNoTracking().Single(); }
    public void SaveSettings(AuctionSession settings)
    {
        if (string.IsNullOrWhiteSpace(settings.TournamentName) || string.IsNullOrWhiteSpace(settings.AuctionName)) throw new InvalidOperationException("Tournament and auction names are required.");
        if (settings.CurrentRound < 1 || settings.DefaultBudgetPaise < 0) throw new InvalidOperationException("Round must be positive and budget non-negative.");
        using var db = Open();
        var old = db.Sessions.Single();
        if (settings.CurrentRound != old.CurrentRound && db.Players.Any()) throw new InvalidOperationException("Use Start Re-Auction to change rounds after importing players.");
        db.Entry(old).CurrentValues.SetValues(settings); db.SaveChanges();
    }
}

public sealed record AuctionSummary(string TournamentName, string AuctionName, int CurrentRound, int PlayerCount, string DatabasePath);

public sealed class AppLog(Workspace workspace)
{
    private readonly object gate = new();
    public void Write(string operation, Exception? exception = null)
    {
        try { lock (gate) { var directory = Path.Combine(workspace.Root, "logs"); Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd}.log"), $"{DateTime.UtcNow:O} {operation} {exception?.GetType().Name}\n{exception?.StackTrace}\n"); } }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class BackupService(Workspace workspace, AppLog log) : IBackupService
{
    public void Backup(string destination)
    {
        if (Path.GetFullPath(destination).Equals(workspace.DatabasePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a different file for the backup.");
        if (File.Exists(destination + "-wal") || File.Exists(destination + "-shm")) throw new InvalidOperationException("This destination has SQLite working files. Choose a new backup filename.");
        var temporary = destination + "." + Guid.NewGuid() + ".tmp";
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = workspace.DatabasePath, Pooling = false }.ToString()))
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
            { source.Open(); target.Open(); source.BackupDatabase(target); using var command = target.CreateCommand(); command.CommandText = "PRAGMA journal_mode=DELETE"; command.ExecuteScalar(); }
            Workspace.ValidateDatabase(temporary);
            File.Move(temporary, destination, true); log.Write("Backup completed");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Restore(string source, string destination)
    {
        Workspace.ValidateDatabase(source);
        if (File.Exists(destination)) throw new InvalidOperationException("Restore to a new filename to preserve existing auctions.");
        using (var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString()))
        { from.Open(); to.Open(); from.BackupDatabase(to); using var command = to.CreateCommand(); command.CommandText = "PRAGMA journal_mode=DELETE"; command.ExecuteScalar(); }
        workspace.Select(destination); log.Write("Restore completed");
    }
}
