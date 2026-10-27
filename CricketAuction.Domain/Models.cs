using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CricketAuction.Domain;
public enum AuctionStatus { Available, Sold, Unsold }
public enum DuplicateResolution { KeepExisting, UpdateExisting, ImportAsNew }
public class Player
{
    public int Id { get; set; }
    public string RegistrationTimestamp { get; set; } = "";
    public string OriginalRegistrationJson { get; set; } = "";
    public string FullName { get; set; } = "";
    public int? Age { get; set; }
    public string PhoneNumber { get; set; } = "";
    public string PlayerType { get; set; } = "";
    public string BattingStyle { get; set; } = "";
    public string BowlingStyle { get; set; } = "";
    public string PlayerPhotoUrl { get; set; } = "";
    public byte[]? Photo { get; set; }
    public bool ManualPhoto { get; set; }
    public string AreaCity { get; set; } = "";
    public string JerseyName { get; set; } = "";
    public string JerseyNumber { get; set; } = "";
    public string JerseySize { get; set; } = "";
    public AuctionStatus Status { get; set; }
    public int AuctionRound { get; set; } = 1;
    public long SoldPaise { get; set; }
    public int? TeamId { get; set; }
    public Team? Team { get; set; }
    public DateTime? SoldAt { get; set; }
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [NotMapped] public string SoldPrice => Money.Format(SoldPaise);
    [NotMapped] public string Jersey => $"{JerseyName} #{JerseyNumber}";
    [NotMapped] public string TeamName => Team?.Name ?? "";
}
public class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public byte[]? Logo { get; set; }
    public long? BudgetPaise { get; set; }
    [NotMapped] public long SpentPaise { get; set; }
    [NotMapped] public int PlayerCount { get; set; }
    [NotMapped] public string Spent => Money.Format(SpentPaise);
    [NotMapped] public string Budget => BudgetPaise.HasValue ? Money.Format(BudgetPaise.Value) : "Unlimited";
    [NotMapped] public string Remaining => BudgetPaise.HasValue ? Money.Format(BudgetPaise.Value - SpentPaise) : "Unlimited";
    [NotMapped] public double BudgetUsagePercent => BudgetPaise is > 0 ? Math.Clamp(SpentPaise * 100d / BudgetPaise.Value, 0, 100) : 0;
    [NotMapped] public string BudgetWarning => !BudgetPaise.HasValue ? "" : SpentPaise > BudgetPaise.Value ? "OVER BUDGET" : SpentPaise == BudgetPaise.Value ? "LIMIT REACHED" : BudgetUsagePercent >= 80 ? "NEAR LIMIT" : "";
    public override string ToString() => Name;
}
public class AuctionSession
{
    public int Id { get; set; } = 1;
    public int SchemaVersion { get; set; } = 1;
    public string TournamentName { get; set; } = "Cricket Tournament";
    public string AuctionName { get; set; } = "Player Auction";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int CurrentRound { get; set; } = 1;
    public bool EnableBudgets { get; set; } = true;
    public long? DefaultBudgetPaise { get; set; }
    public string Currency { get; set; } = "INR";
    public string Theme { get; set; } = "System";
    public string PhotoCacheLocation { get; set; } = "";
    public string ExportLocation { get; set; } = "";
    public string PptLocation { get; set; } = "";
}
public class AuctionHistory
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public Player? Player { get; set; }
    public Guid ActionId { get; set; }
    public Guid? ReversesActionId { get; set; }
    public string Action { get; set; } = "";
    public int AuctionRound { get; set; }
    public AuctionStatus Status { get; set; }
    public long SoldPaise { get; set; }
    public int? TeamId { get; set; }
    public string TeamName { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = "";
    public string BeforeState { get; set; } = "";
    public int BeforeRound { get; set; }
    [NotMapped] public string Amount => Money.Format(SoldPaise);
    [NotMapped] public DateTime LocalTime => Timestamp.ToLocalTime();
}
public record AuctionState(AuctionStatus Status, int Round, long Paise, int? TeamId, DateTime? SoldAt, string Notes)
{
    public static AuctionState Of(Player p) => new(p.Status, p.AuctionRound, p.SoldPaise, p.TeamId, p.SoldAt, p.Notes);
    public void Apply(Player p) { p.Status = Status; p.AuctionRound = Round; p.SoldPaise = Paise; p.TeamId = TeamId; p.SoldAt = SoldAt; p.Notes = Notes; }
}
public static class Money
{
    public static string Format(long paise) => "₹" + (paise / 100m).ToString(paise % 100 == 0 ? "N0" : "N2", CultureInfo.GetCultureInfo("en-IN"));
    public static long Parse(string text)
    {
        if (!decimal.TryParse(text.Replace("₹", "").Trim(), NumberStyles.Number, CultureInfo.GetCultureInfo("en-IN"), out var amount)
            || amount < 0 || amount > 1_000_000_000 || decimal.Round(amount, 2) != amount)
            throw new InvalidOperationException("Enter a valid non-negative rupee amount with at most two decimal places.");
        return checked((long)(amount * 100));
    }
}
public static class PlayerRules
{
    public static string PhoneKey(string value)
    {
        var digits = Regex.Replace(value, "[^0-9]", "");
        return digits.Length == 12 && digits.StartsWith("91", StringComparison.Ordinal) ? digits[2..] : digits;
    }
    public static string NameKey(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
    public static bool Duplicate(Player a, Player b) =>
        NameKey(a.FullName) == NameKey(b.FullName) &&
        PhoneKey(a.PhoneNumber).Length > 0 && PhoneKey(a.PhoneNumber) == PhoneKey(b.PhoneNumber);
    public static string? Validate(Player p)
    {
        if (string.IsNullOrWhiteSpace(p.FullName)) return "Player name is required.";
        if (p.FullName.Length > 200) return "Player name must be at most 200 characters.";
        if (!Regex.IsMatch(p.PhoneNumber, @"^\+?[0-9 ()-]+$") || PhoneKey(p.PhoneNumber).Length is < 7 or > 15) return "Phone must contain 7–15 digits.";
        if (p.Age is < 5 or > 100) return "Age must be between 5 and 100.";
        if (p.JerseyNumber.Length > 0 && (!int.TryParse(p.JerseyNumber, out var jersey) || jersey is < 0 or > 999)) return "Jersey number must be between 0 and 999.";
        return null;
    }
    public static bool Matches(Player p, string query) => new[] { p.FullName, p.PhoneNumber, PhoneKey(p.PhoneNumber), p.JerseyName, p.JerseyNumber }
        .Any(x => x.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
}
public record Statistics(int Total, int Sold, int Unsold, int Available, long TotalPaise, long AveragePaise, long HighestPaise)
{
    public static Statistics Calculate(IEnumerable<Player> players)
    {
        var all = players.ToList(); var sold = all.Where(p => p.Status == AuctionStatus.Sold).ToList();
        return new(all.Count, sold.Count, all.Count(p => p.Status == AuctionStatus.Unsold), all.Count(p => p.Status == AuctionStatus.Available),
            sold.Sum(p => p.SoldPaise), sold.Count == 0 ? 0 : (long)Math.Round(sold.Average(p => p.SoldPaise)), sold.Count == 0 ? 0 : sold.Max(p => p.SoldPaise));
    }
}
public class ImportRow
{
    public int RowNumber { get; set; }
    public Player Player { get; set; } = new();
    public int? ExistingId { get; set; }
    public bool IsDuplicate { get; set; }
    public string Error { get; set; } = "";
    public string Warning { get; set; } = "";
    public DuplicateResolution Resolution { get; set; } = DuplicateResolution.KeepExisting;
    public string Result => Error.Length > 0 ? Error : (IsDuplicate ? "Same name and phone: review" : "Valid") + (Warning.Length > 0 ? " — " + Warning : "");
}
public interface IAuctionService
{
    void Sell(int playerId, int teamId, long paise, string notes);
    void MarkUnsold(int playerId);
    int StartNextRound();
    string LastActionDescription();
    void Undo();
}
public interface IPlayerService { List<Player> Players(); void SavePlayer(Player player); List<AuctionHistory> History(int? playerId = null); }
public interface ITeamService { List<Team> Teams(); void SaveTeam(Team team); }
public interface IImportService { List<ImportRow> Preview(string path); int Import(IReadOnlyList<ImportRow> rows); }
public interface IExportService { void Export(string path, IReadOnlyList<Player> players, bool complete); }
public interface IPowerPointService { void Generate(string path, IReadOnlyList<Player> players, string tournament, int round); }
public sealed record PhotoFetchResult(byte[]? Bytes, string? Error)
{
    public bool Succeeded => Bytes is { Length: > 0 };
}
public interface IImageService
{
    Task<PhotoFetchResult> FetchWithResultAsync(string url, CancellationToken cancellationToken = default);
    Task<byte[]?> FetchAsync(string url, CancellationToken cancellationToken = default);
}
public interface IBackupService { void Backup(string destination); void Restore(string source, string destination); }
