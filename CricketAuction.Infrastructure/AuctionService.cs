using System.Text.Json;
using CricketAuction.Domain;
using Microsoft.EntityFrameworkCore;

namespace CricketAuction.Infrastructure;

public sealed class AuctionService(Workspace workspace, AppLog log) : IAuctionService, IPlayerService, ITeamService
{
    public List<Player> Players() { using var db = workspace.Open(); return db.Players.Include(p => p.Team).AsNoTracking().OrderBy(p => p.FullName).ToList(); }
    public List<AuctionHistory> History(int? playerId = null)
    {
        using var db = workspace.Open(); return db.History.Include(h => h.Player).AsNoTracking().Where(h => playerId == null || h.PlayerId == playerId).OrderByDescending(h => h.Id).ToList();
    }
    public List<Team> Teams()
    {
        using var db = workspace.Open(); var teams = db.Teams.AsNoTracking().OrderBy(t => t.Name).ToList();
        var sales = db.Players.Where(p => p.Status == AuctionStatus.Sold).Select(p => new { p.TeamId, p.SoldPaise }).ToList();
        foreach (var team in teams) { team.SpentPaise = sales.Where(p => p.TeamId == team.Id).Sum(p => p.SoldPaise); team.PlayerCount = sales.Count(p => p.TeamId == team.Id); }
        return teams;
    }
    public void SaveTeam(Team team)
    {
        team.Name = team.Name.Trim();
        if (team.Name.Length == 0 || team.BudgetPaise < 0) throw new InvalidOperationException("A team name and a non-negative budget are required.");
        using var db = workspace.Open(); using var tx = db.Database.BeginTransaction();
        if (db.Teams.Any(t => t.Id != team.Id && t.Name.ToLower() == team.Name.ToLower())) throw new InvalidOperationException("A team with that name already exists.");
        var spent = db.Players.Where(p => p.TeamId == team.Id).Sum(p => p.SoldPaise);
        if (db.Sessions.Single().EnableBudgets && team.BudgetPaise < spent) throw new InvalidOperationException("Budget cannot be lower than this team's spending.");
        if (team.Id == 0) db.Teams.Add(team); else { var current = db.Teams.Single(t => t.Id == team.Id); db.Entry(current).CurrentValues.SetValues(team); }
        db.SaveChanges(); tx.Commit(); log.Write("Team saved");
    }
    public static void CopyRegistration(Player source, Player target)
    {
        target.FullName = source.FullName.Trim(); target.Age = source.Age; target.PhoneNumber = source.PhoneNumber.Trim();
        target.PlayerType = source.PlayerType; target.BattingStyle = source.BattingStyle; target.BowlingStyle = source.BowlingStyle;
        if (target.PlayerPhotoUrl != source.PlayerPhotoUrl && !target.ManualPhoto) target.Photo = null;
        target.PlayerPhotoUrl = source.PlayerPhotoUrl; target.AreaCity = source.AreaCity; target.JerseyName = source.JerseyName;
        target.JerseyNumber = source.JerseyNumber; target.JerseySize = source.JerseySize; target.UpdatedAt = DateTime.UtcNow;
    }
    public void SavePlayer(Player player)
    {
        if (PlayerRules.Validate(player) is { } error) throw new InvalidOperationException(error);
        using var db = workspace.Open();
        if (db.Players.AsNoTracking().ToList().Any(p => p.Id != player.Id && PlayerRules.Duplicate(p, player))) throw new InvalidOperationException("A player with this phone or name and jersey already exists. Use import preview for intentional duplicates.");
        if (player.Id == 0) { player.Status = AuctionStatus.Available; player.AuctionRound = db.Sessions.Single().CurrentRound; player.SoldPaise = 0; player.TeamId = null; player.Team = null; db.Players.Add(player); }
        else CopyRegistration(player, db.Players.Single(p => p.Id == player.Id));
        db.SaveChanges(); log.Write("Player information saved");
    }
    public void SavePhoto(int id, byte[] bytes, bool manual)
    {
        using var db = workspace.Open(); var player = db.Players.Single(p => p.Id == id);
        if (player.ManualPhoto && !manual) return;
        DeleteOriginalPhotoIfPresent(db, id);
        player.Photo = bytes; player.ManualPhoto = manual; player.UpdatedAt = DateTime.UtcNow; db.SaveChanges();
        var cache = db.Sessions.Single().PhotoCacheLocation;
        if (!string.IsNullOrWhiteSpace(cache))
        {
            try { Directory.CreateDirectory(cache); File.WriteAllBytes(Path.Combine(cache, $"{Path.GetFileNameWithoutExtension(workspace.DatabasePath)}_player_{id}.png"), bytes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { log.Write("Optional photo cache copy failed; database photo retained", ex); }
        }
    }
    public void SaveRotatedPhoto(int id, byte[] original, byte[] rotated)
    {
        using var db = workspace.Open(); using var transaction = db.Database.BeginTransaction();
        EnsurePhotoOriginalsTable(db);
        db.Database.ExecuteSqlInterpolated($"INSERT OR IGNORE INTO PhotoOriginals (PlayerId, Photo) VALUES ({id}, {original})");
        var player = db.Players.Single(p => p.Id == id); player.Photo = rotated; player.ManualPhoto = true; player.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges(); transaction.Commit(); log.Write("Player photo rotated");
    }
    public bool HasOriginalPhoto(int id)
    {
        using var db = workspace.Open(); var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PhotoOriginals'";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0) return false;
        command.CommandText = "SELECT COUNT(*) FROM PhotoOriginals WHERE PlayerId = $playerId";
        var parameter = command.CreateParameter(); parameter.ParameterName = "$playerId"; parameter.Value = id; command.Parameters.Add(parameter);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }
    public bool RestoreOriginalPhoto(int id)
    {
        using var db = workspace.Open(); using var transaction = db.Database.BeginTransaction();
        var original = ReadOriginalPhoto(db, id);
        if (original == null) return false;
        var player = db.Players.Single(p => p.Id == id); player.Photo = original; player.ManualPhoto = true; player.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges();
        db.Database.ExecuteSqlInterpolated($"DELETE FROM PhotoOriginals WHERE PlayerId={id}");
        transaction.Commit(); log.Write("Original player photo restored"); return true;
    }
    private static void EnsurePhotoOriginalsTable(AuctionDb db) => db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS PhotoOriginals (PlayerId INTEGER PRIMARY KEY REFERENCES Players(Id) ON DELETE CASCADE, Photo BLOB NOT NULL)");
    private static void DeleteOriginalPhotoIfPresent(AuctionDb db, int id)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PhotoOriginals'";
        if (Convert.ToInt32(command.ExecuteScalar()) > 0) db.Database.ExecuteSqlInterpolated($"DELETE FROM PhotoOriginals WHERE PlayerId={id}");
    }
    private static byte[]? ReadOriginalPhoto(AuctionDb db, int id)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PhotoOriginals'";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0) return null;
        command.CommandText = "SELECT Photo FROM PhotoOriginals WHERE PlayerId = $playerId";
        var parameter = command.CreateParameter(); parameter.ParameterName = "$playerId"; parameter.Value = id; command.Parameters.Add(parameter);
        return command.ExecuteScalar() as byte[];
    }
    private static void Record(AuctionDb db, Player p, AuctionState before, string action, Guid actionId, int beforeRound, string team = "", Guid? reverses = null)
    {
        p.UpdatedAt = DateTime.UtcNow;
        db.History.Add(new() { PlayerId = p.Id, ActionId = actionId, ReversesActionId = reverses, Action = action,
            AuctionRound = p.AuctionRound, Status = p.Status, SoldPaise = p.SoldPaise, TeamId = p.TeamId, TeamName = team,
            Notes = p.Notes, BeforeState = JsonSerializer.Serialize(before), BeforeRound = beforeRound });
    }
    public void Sell(int playerId, int teamId, long paise, string notes)
    {
        if (paise <= 0 || paise > 100_000_000_000) throw new InvalidOperationException("Sold amount must be greater than zero and at most ₹1,00,00,00,000.");
        using var db = workspace.Open(); using var tx = db.Database.BeginTransaction();
        var p = db.Players.Single(p => p.Id == playerId); var team = db.Teams.Single(t => t.Id == teamId); var session = db.Sessions.Single();
        if (p.Status != AuctionStatus.Available) throw new InvalidOperationException("Only available players can be sold. Start a re-auction for unsold players, or undo an incorrect action.");
        var spent = db.Players.Where(p => p.TeamId == teamId && p.Status == AuctionStatus.Sold).Sum(p => p.SoldPaise);
        if (session.EnableBudgets && team.BudgetPaise.HasValue && spent + paise > team.BudgetPaise.Value)
            throw new InvalidOperationException($"{team.Name} has only {Money.Format(team.BudgetPaise.Value - spent)} remaining. Cannot complete sale for {Money.Format(paise)}.");
        var before = AuctionState.Of(p); p.Status = AuctionStatus.Sold; p.SoldPaise = paise; p.TeamId = teamId;
        p.SoldAt = DateTime.UtcNow; p.Notes = notes;
        Record(db, p, before, "Sold", Guid.NewGuid(), session.CurrentRound, team.Name);
        db.SaveChanges(); tx.Commit(); log.Write($"Sold player id={playerId}");
    }
    public void MarkUnsold(int playerId)
    {
        using var db = workspace.Open(); using var tx = db.Database.BeginTransaction(); var p = db.Players.Single(p => p.Id == playerId);
        if (p.Status != AuctionStatus.Available) throw new InvalidOperationException("Only available players can be marked unsold.");
        var before = AuctionState.Of(p); p.Status = AuctionStatus.Unsold;
        Record(db, p, before, "Unsold", Guid.NewGuid(), db.Sessions.Single().CurrentRound);
        db.SaveChanges(); tx.Commit(); log.Write($"Unsold player id={playerId}");
    }
    public int StartNextRound()
    {
        using var db = workspace.Open(); using var tx = db.Database.BeginTransaction(); var session = db.Sessions.Single();
        var unsold = db.Players.Where(p => p.Status == AuctionStatus.Unsold).ToList();
        if (unsold.Count == 0) throw new InvalidOperationException("There are no unsold players to re-auction.");
        if (db.Players.Any(p => p.Status == AuctionStatus.Available)) throw new InvalidOperationException("Finish the available players in this round before starting the next round.");
        var oldRound = session.CurrentRound; session.CurrentRound++; var action = Guid.NewGuid();
        foreach (var p in unsold) { var before = AuctionState.Of(p); p.Status = AuctionStatus.Available; p.AuctionRound = session.CurrentRound; Record(db, p, before, "Re-auction", action, oldRound); }
        db.SaveChanges(); tx.Commit(); log.Write("Re-auction started"); return unsold.Count;
    }
    private static List<AuctionHistory> UndoTarget(AuctionDb db)
    {
        var history = db.History.OrderByDescending(h => h.Id).ToList();
        var undone = history.Where(h => h.ReversesActionId.HasValue).Select(h => h.ReversesActionId!.Value).ToHashSet();
        var last = history.FirstOrDefault(h => h.ReversesActionId == null && !undone.Contains(h.ActionId));
        return last == null ? [] : history.Where(h => h.ActionId == last.ActionId).ToList();
    }
    public string LastActionDescription()
    {
        using var db = workspace.Open(); var actions = UndoTarget(db); if (actions.Count == 0) return "No action to undo.";
        var h = actions[0]; return actions.Count > 1 ? $"Re-auction: {actions.Count} players, round {h.AuctionRound}" : $"{db.Players.Single(p => p.Id == h.PlayerId).FullName} • {h.Action} • {h.TeamName} {Money.Format(h.SoldPaise)}";
    }
    public void Undo()
    {
        using var db = workspace.Open(); using var tx = db.Database.BeginTransaction(); var actions = UndoTarget(db);
        if (actions.Count == 0) throw new InvalidOperationException("There are no auction actions to undo.");
        var session = db.Sessions.Single(); var undoId = Guid.NewGuid(); var currentRound = session.CurrentRound;
        if (actions[0].Action == "Re-auction")
        {
            var ids = actions.Select(a => a.PlayerId).ToHashSet();
            if (db.Players.Any(p => p.AuctionRound == currentRound && !ids.Contains(p.Id))) throw new InvalidOperationException("Players were imported in this round. Undoing the round would make their round inconsistent.");
            session.CurrentRound = actions[0].BeforeRound;
        }
        foreach (var h in actions)
        {
            var p = db.Players.Single(p => p.Id == h.PlayerId); var before = AuctionState.Of(p);
            JsonSerializer.Deserialize<AuctionState>(h.BeforeState)!.Apply(p);
            Record(db, p, before, "Undo " + h.Action, undoId, currentRound, p.TeamId.HasValue ? db.Teams.Single(t => t.Id == p.TeamId).Name : "", h.ActionId);
        }
        db.SaveChanges(); tx.Commit(); log.Write("Auction action undone");
    }
}
