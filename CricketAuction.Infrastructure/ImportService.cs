using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CricketAuction.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;

namespace CricketAuction.Infrastructure;
public sealed class ImportService(Workspace workspace, AppLog log) : IImportService
{
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["FullName"] = ["fullname", "name", "playername", "nameoftheplayer"],
        ["PhoneNumber"] = ["phonenumber", "phone", "mobile", "mobilenumber", "contactnumber", "whatsappnumber"],
        ["Age"] = ["age", "playerage"], ["PlayerType"] = ["playertype", "type", "role", "category", "playerrole"],
        ["BattingStyle"] = ["battingstyle", "batting", "bats"], ["BowlingStyle"] = ["bowlingstyle", "bowling", "bowls"],
        ["PlayerPhotoUrl"] = ["playerphoto", "photo", "photourl", "playerphotourl", "uploadphoto", "uploadyourphoto", "photograph"],
        ["AreaCity"] = ["areacity", "area", "city", "location", "place"],
        ["JerseyName"] = ["jerseyname", "nameonjersey"], ["JerseyNumber"] = ["jerseynumber", "jerseyno", "shirtnumber"],
        ["JerseySize"] = ["jerseysize", "shirtsize", "tshirtsize", "size"], ["RegistrationTimestamp"] = ["timestamp", "registrationtimestamp", "registrationdate", "date"]
    };
    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", "");
    public static string? MapColumn(string header)
    {
        var key = Normalize(header);
        foreach (var pair in Aliases) if (pair.Value.Contains(key)) return pair.Key;
        foreach (var pair in Aliases) if (pair.Value.Any(a => a.Length >= 6 && key.StartsWith(a))) return pair.Key;
        return null;
    }
    public List<ImportRow> Preview(string path)
    {
        List<string[]> records = [];
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            using var parser = new TextFieldParser(path, Encoding.UTF8) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
            parser.SetDelimiters(",");
            while (!parser.EndOfData) records.Add(parser.ReadFields() ?? []);
        }
        else
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var workbook = new XLWorkbook(source); var sheet = workbook.Worksheets.First();
            var last = sheet.LastRowUsed()?.RowNumber() ?? 0; var columns = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (last > 100_001 || columns > 200) throw new InvalidOperationException("Import supports at most 100,000 rows and 200 columns.");
            for (var r = 1; r <= last; r++) records.Add(Enumerable.Range(1, columns).Select(c => sheet.Cell(r, c).HasHyperlink ? sheet.Cell(r, c).GetHyperlink().ExternalAddress?.ToString() ?? sheet.Cell(r, c).GetFormattedString() : sheet.Cell(r, c).GetFormattedString()).ToArray());
        }
        if (records.Count < 2) throw new InvalidOperationException("The file has no player rows. Put column names in the first row.");
        var headers = records[0]; var mapping = headers.Select(MapColumn).ToArray();
        if (!mapping.Contains("FullName") || !mapping.Contains("PhoneNumber")) throw new InvalidOperationException("Missing required columns: Full Name and Phone Number. Accepted aliases include Name, Player Name, Phone and Mobile.");
        if (mapping.Where(m => m != null).GroupBy(m => m).Any(g => g.Count() > 1)) throw new InvalidOperationException("Multiple columns map to the same field. Rename or remove the repeated column before importing.");
        using var db = workspace.Open(); var existing = db.Players.AsNoTracking().ToList(); var seen = new List<Player>(); var result = new List<ImportRow>();
        for (var index = 1; index < records.Count; index++)
        {
            var values = records[index]; if (values.All(string.IsNullOrWhiteSpace)) continue;
            var row = new ImportRow { RowNumber = index + 1 }; var original = new Dictionary<string, string>();
            for (var c = 0; c < headers.Length; c++)
            {
                var value = c < values.Length ? values[c].Trim() : ""; original[$"{c + 1}:{headers[c]}"] = value;
                if (mapping[c] is not { } property) continue;
                if (property == "Age")
                {
                    if (value.Length > 0) { if (int.TryParse(value, out var age)) row.Player.Age = age; else row.Error = "Age must be a whole number."; }
                }
                else typeof(Player).GetProperty(property)!.SetValue(row.Player, value);
            }
            row.Player.OriginalRegistrationJson = JsonSerializer.Serialize(original);
            // Correct unambiguous form-entry mistakes while retaining the source values.
            var jersey = row.Player.JerseyNumber;
            if (Regex.IsMatch(jersey, @"^[oO0-9]{1,3}$") && jersey.Any(c => c is 'o' or 'O'))
            {
                row.Player.JerseyNumber = jersey.Replace('o', '0').Replace('O', '0');
                row.Warning = $"Jersey {jersey} corrected to {row.Player.JerseyNumber}.";
            }
            else if (PlayerRules.NameKey(jersey) == PlayerRules.NameKey(row.Player.FullName)
                && int.TryParse(row.Player.JerseyName, out var swappedNumber) && swappedNumber is >= 0 and <= 999)
            {
                row.Player.JerseyNumber = row.Player.JerseyName;
                row.Player.JerseyName = jersey;
                row.Warning = "Swapped jersey name and number corrected.";
            }
            row.Error = row.Error.Length > 0 ? row.Error : PlayerRules.Validate(row.Player) ?? "";
            var match = existing.Where(p => PlayerRules.Duplicate(p, row.Player))
                .OrderByDescending(p => PlayerRules.NameKey(p.FullName) == PlayerRules.NameKey(row.Player.FullName)).FirstOrDefault();
            row.ExistingId = match?.Id; row.IsDuplicate = match != null || seen.Any(p => PlayerRules.Duplicate(p, row.Player));
            if (row.Error.Length == 0) seen.Add(row.Player);
            result.Add(row);
        }
        log.Write($"Import preview rows={result.Count}"); return result;
    }
    public int Import(IReadOnlyList<ImportRow> rows)
    {
        using var db = workspace.Open(); using var tx = db.Database.BeginTransaction(); var all = db.Players.ToList(); var round = db.Sessions.Single().CurrentRound; var count = 0;
        foreach (var row in rows.Where(r => r.Error.Length == 0))
        {
            if (PlayerRules.Validate(row.Player) is { } error) throw new InvalidOperationException($"Row {row.RowNumber}: {error}");
            var matches = all.Where(p => PlayerRules.Duplicate(p, row.Player)).ToList();
            var sameName = matches.Where(p => PlayerRules.NameKey(p.FullName) == PlayerRules.NameKey(row.Player.FullName)).ToList();
            if (sameName.Count > 0) matches = sameName;
            if (matches.Count > 0)
            {
                if (!row.IsDuplicate) throw new InvalidOperationException("Player data changed since preview. Preview the file again.");
                if (row.Resolution == DuplicateResolution.KeepExisting) continue;
                if (row.Resolution == DuplicateResolution.UpdateExisting)
                {
                    if (matches.Count > 1) throw new InvalidOperationException($"Row {row.RowNumber} matches multiple players. Keep existing or explicitly import as new.");
                    AuctionService.CopyRegistration(row.Player, matches[0]); count++; continue;
                }
            }
            var p = new Player { RegistrationTimestamp = row.Player.RegistrationTimestamp, OriginalRegistrationJson = row.Player.OriginalRegistrationJson, AuctionRound = round };
            AuctionService.CopyRegistration(row.Player, p); db.Players.Add(p); all.Add(p); count++;
        }
        db.SaveChanges(); tx.Commit(); log.Write($"Import completed count={count}"); return count;
    }
}
