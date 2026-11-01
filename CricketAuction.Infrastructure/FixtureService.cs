using System.Text.Json;
using CricketAuction.Domain;
using Microsoft.EntityFrameworkCore;

namespace CricketAuction.Infrastructure;

public enum TournamentFormat { League, Knockout, PoolPlay }
public enum FixtureStatus { Scheduled, InProgress, Completed, Abandoned }

public sealed class FixtureRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int MatchNumber { get; set; }
    public int Round { get; set; }
    public string RoundName { get; set; } = "Round 1";
    public string PoolName { get; set; } = "";
    public int? TeamAId { get; set; }
    public int? TeamBId { get; set; }
    public string TeamASeed { get; set; } = "";
    public string TeamBSeed { get; set; } = "";
    public DateTime ScheduledAt { get; set; }
    public string Ground { get; set; } = "";
    public FixtureStatus Status { get; set; } = FixtureStatus.Scheduled;
    public string Result { get; set; } = "";
    public int? WinnerTeamId { get; set; }
    public int BracketIndex { get; set; }
    public bool IsBye { get; set; }
    public int DurationMinutes { get; set; } = 180;
}

public sealed record PoolStanding(string Pool, int TeamId, int Position, int Played, int Wins, int Losses, int Points, bool Tied, bool PoolComplete);

public sealed class FixtureSchedule
{
    public int MinimumRestMinutes { get; set; } = 60;
    public TournamentFormat Format { get; set; }
    public List<int> SeedOrderTeamIds { get; set; } = [];
    public List<FixtureRecord> Fixtures { get; set; } = [];
}

public sealed class FixtureService(Workspace workspace)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public FixtureSchedule Load()
    {
        using var db = workspace.Open();
        EnsureTable(db);
        var connection = db.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Data FROM TournamentFixtures WHERE Id = 1";
        var data = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(data)) return new();
        var schedule = JsonSerializer.Deserialize<FixtureSchedule>(data, JsonOptions) ?? new();
        if (schedule.Format == TournamentFormat.PoolPlay) RecalculatePoolQualifiers(schedule);
        return schedule;
    }

    public void Save(FixtureSchedule schedule)
    {
        using var db = workspace.Open();
        EnsureTable(db);
        using var transaction = db.Database.BeginTransaction();
        var data = JsonSerializer.Serialize(schedule, JsonOptions);
        db.Database.ExecuteSqlInterpolated($"INSERT INTO TournamentFixtures (Id, Data) VALUES (1, {data}) ON CONFLICT(Id) DO UPDATE SET Data = excluded.Data");
        transaction.Commit();
    }

    public static FixtureSchedule Generate(IReadOnlyList<Team> teams, TournamentFormat format, DateTime startDate,
        TimeSpan firstMatchTime, int daysBetweenRounds, IReadOnlyList<string> grounds, bool doubleRoundRobin,
        IReadOnlyDictionary<string, IReadOnlyList<int>>? poolAssignments = null, int matchDurationMinutes = 40)
    {
        if (teams.Count < 2) throw new InvalidOperationException("At least two teams are needed to create fixtures.");
        if (daysBetweenRounds < 0) throw new InvalidOperationException("Days between rounds cannot be negative.");
        if (matchDurationMinutes < 1 || matchDurationMinutes > 1440) throw new InvalidOperationException("Match duration must be between 1 and 1440 minutes.");
        if (firstMatchTime < TimeSpan.Zero || firstMatchTime >= TimeSpan.FromDays(1)) throw new InvalidOperationException("Choose a start time within the day.");
        var cleanGrounds = grounds.Select(ground => ground.Trim()).Where(ground => ground.Length > 0).ToList();
        var schedule = new FixtureSchedule { Format = format };
        var matchNumber = 1;
        if (format == TournamentFormat.League)
        {
            var rounds = BuildLeagueRounds(teams.Select(team => team.Id).ToList());
            if (doubleRoundRobin)
                rounds.AddRange(rounds.Select(round => round.Select(pair => (pair.Away, pair.Home)).ToList()).ToList());
            for (var roundIndex = 0; roundIndex < rounds.Count; roundIndex++)
            {
                var date = startDate.Date.AddDays(roundIndex * daysBetweenRounds);
                var fixturesInRound = rounds[roundIndex];
                for (var index = 0; index < fixturesInRound.Count; index++)
                {
                    var pair = fixturesInRound[index];
                    schedule.Fixtures.Add(new FixtureRecord
                    {
                        MatchNumber = matchNumber++, Round = roundIndex + 1, RoundName = $"League · Round {roundIndex + 1}",
                        TeamAId = pair.Home, TeamBId = pair.Away,
                        ScheduledAt = date.Add(firstMatchTime).AddHours(index * 3), Ground = PickGround(cleanGrounds, matchNumber - 2)
                    });
                }
            }
        }
        else if (format == TournamentFormat.PoolPlay)
        {
            if (teams.Count < 4) throw new InvalidOperationException("Pools + playoffs requires at least four teams.");
            var ordered = teams.OrderBy(team => team.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var assignments = poolAssignments ?? DefaultPoolAssignments(ordered, 2);
            var teamIds = ordered.Select(team => team.Id).ToHashSet();
            if (assignments.Count < 2 || assignments.Any(pool => pool.Value.Count < 2)
                || assignments.Values.SelectMany(ids => ids).GroupBy(id => id).Any(group => group.Count() != 1)
                || !assignments.Values.SelectMany(ids => ids).ToHashSet().SetEquals(teamIds))
                throw new InvalidOperationException("Assign every team to exactly one pool, with at least two teams in each pool.");
            var pools = assignments.OrderBy(pool => pool.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(pool => (Name: pool.Key, Teams: ordered.Where(team => pool.Value.Contains(team.Id)).ToList())).ToList();
            schedule.SeedOrderTeamIds = pools.SelectMany(pool => pool.Teams).Select(team => team.Id).ToList();
            var poolRounds = pools.Select(pool => (pool.Name, Rounds: BuildLeagueRounds(pool.Teams.Select(team => team.Id).ToList()))).ToList();
            var roundCount = poolRounds.Max(pool => pool.Rounds.Count);
            for (var roundIndex = 0; roundIndex < roundCount; roundIndex++)
            {
                var matchIndex = 0;
                foreach (var pool in poolRounds.Where(pool => roundIndex < pool.Rounds.Count))
                foreach (var pair in pool.Rounds[roundIndex])
                {
                    schedule.Fixtures.Add(new FixtureRecord
                    {
                        MatchNumber = matchNumber++, Round = roundIndex + 1, RoundName = pool.Name, PoolName = pool.Name,
                        TeamAId = pair.Home, TeamBId = pair.Away,
                        ScheduledAt = startDate.Date.AddDays(roundIndex * daysBetweenRounds).Add(firstMatchTime).AddHours(matchIndex++ * 3),
                        Ground = PickGround(cleanGrounds, matchNumber - 2)
                    });
                }
            }
            if (pools.Count == 2)
            {
                var semiRound = roundCount + 1;
                var playoffDate = startDate.Date.AddDays(roundCount * daysBetweenRounds).Add(firstMatchTime);
                schedule.Fixtures.Add(new FixtureRecord
                {
                    MatchNumber = matchNumber++, Round = semiRound, RoundName = "Semi-finals", BracketIndex = 0,
                    TeamASeed = $"{pools[0].Name} 1st", TeamBSeed = $"{pools[1].Name} 2nd", ScheduledAt = playoffDate,
                    Ground = PickGround(cleanGrounds, matchNumber - 2)
                });
                schedule.Fixtures.Add(new FixtureRecord
                {
                    MatchNumber = matchNumber++, Round = semiRound, RoundName = "Semi-finals", BracketIndex = 1,
                    TeamASeed = $"{pools[1].Name} 1st", TeamBSeed = $"{pools[0].Name} 2nd", ScheduledAt = playoffDate.AddHours(3),
                    Ground = PickGround(cleanGrounds, matchNumber - 2)
                });
                var finalsDate = playoffDate.AddDays(daysBetweenRounds);
                schedule.Fixtures.Add(new FixtureRecord
                {
                    MatchNumber = matchNumber++, Round = semiRound + 1, RoundName = "3rd place", BracketIndex = 0,
                    TeamASeed = "Semi-final 1 loser", TeamBSeed = "Semi-final 2 loser", ScheduledAt = finalsDate,
                    Ground = PickGround(cleanGrounds, matchNumber - 2)
                });
                schedule.Fixtures.Add(new FixtureRecord
                {
                    MatchNumber = matchNumber, Round = semiRound + 1, RoundName = "Final", BracketIndex = 1,
                    TeamASeed = "Semi-final 1 winner", TeamBSeed = "Semi-final 2 winner", ScheduledAt = finalsDate.AddHours(3),
                    Ground = PickGround(cleanGrounds, matchNumber - 1)
                });
            }
        }
        else
        {
            var bracketSize = 1;
            while (bracketSize < teams.Count) bracketSize *= 2;
            var roundCount = (int)Math.Log2(bracketSize);
            var slots = new int?[bracketSize];
            for (var i = 0; i < teams.Count; i++) slots[i] = teams[i].Id;
            var roundOneCount = bracketSize / 2;
            for (var index = 0; index < roundOneCount; index++)
            {
                var teamA = slots[index];
                var teamB = slots[bracketSize - index - 1];
                var bye = teamA.HasValue != teamB.HasValue;
                schedule.Fixtures.Add(new FixtureRecord
                {
                    MatchNumber = matchNumber++, Round = 1, RoundName = RoundName(roundCount, 1),
                    TeamAId = teamA, TeamBId = teamB, BracketIndex = index, IsBye = bye,
                    Status = bye ? FixtureStatus.Completed : FixtureStatus.Scheduled,
                    Result = bye ? "Bye" : "", WinnerTeamId = bye ? teamA ?? teamB : null,
                    ScheduledAt = startDate.Date.Add(firstMatchTime).AddHours(index * 3), Ground = PickGround(cleanGrounds, index)
                });
            }
            for (var round = 2; round <= roundCount; round++)
            {
                var fixtureCount = bracketSize / (1 << round);
                for (var index = 0; index < fixtureCount; index++)
                {
                    schedule.Fixtures.Add(new FixtureRecord
                    {
                        MatchNumber = matchNumber++, Round = round, RoundName = RoundName(roundCount, round), BracketIndex = index,
                        ScheduledAt = startDate.Date.AddDays((round - 1) * daysBetweenRounds).Add(firstMatchTime).AddHours(index * 3),
                        Ground = PickGround(cleanGrounds, index)
                    });
                }
            }
            AdvanceAutomaticByes(schedule);
        }
        foreach (var fixture in schedule.Fixtures) fixture.DurationMinutes = matchDurationMinutes;
        if (daysBetweenRounds == 0)
        {
            schedule.MinimumRestMinutes = matchDurationMinutes;
            ScheduleSingleDay(schedule, startDate.Date.Add(firstMatchTime), cleanGrounds, matchDurationMinutes);
        }
        return schedule;
    }

    private static void ScheduleSingleDay(FixtureSchedule schedule, DateTime start, IReadOnlyList<string> grounds, int minutes)
    {
        var ordered = new List<FixtureRecord>();
        var next = start;
        var available = new Dictionary<int, DateTime>();
        var poolMatches = schedule.Fixtures.Where(f => f.PoolName.Length > 0).ToList();
        var poolNames = poolMatches.Select(f => f.PoolName).Distinct().OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var nextPool = 0;
        DateTime Ready(FixtureRecord match) => new[] { match.TeamAId, match.TeamBId }.OfType<int>()
            .Select(id => available.GetValueOrDefault(id, start)).DefaultIfEmpty(start).Max();
        void Place(FixtureRecord match)
        {
            if (!match.IsBye && Ready(match) > next) next = Ready(match);
            match.ScheduledAt = next;
            match.MatchNumber = ordered.Count + 1;
            match.Ground = PickGround(grounds, ordered.Count);
            ordered.Add(match);
            if (match.IsBye) return;
            next = next.AddMinutes(minutes);
            foreach (var id in new[] { match.TeamAId, match.TeamBId }.OfType<int>()) available[id] = next.AddMinutes(minutes);
        }
        while (poolMatches.Count > 0)
        {
            FixtureRecord? chosen = null;
            for (var offset = 0; offset < poolNames.Count; offset++)
            {
                var index = (nextPool + offset) % poolNames.Count;
                chosen = poolMatches.FirstOrDefault(f => f.PoolName == poolNames[index] && Ready(f) <= next);
                if (chosen == null) continue;
                nextPool = (index + 1) % poolNames.Count;
                break;
            }
            if (chosen == null) { next = poolMatches.Min(Ready); continue; }
            Place(chosen); poolMatches.Remove(chosen);
        }
        var remaining = schedule.Fixtures.Where(f => f.PoolName.Length == 0).OrderBy(f => f.Round).ThenBy(f => f.MatchNumber).GroupBy(f => f.Round);
        foreach (var round in remaining)
        {
            // Unknown qualifiers may have played in the last slot of the preceding stage.
            if (ordered.Count > 0 && schedule.Format != TournamentFormat.League) next = next.AddMinutes(minutes);
            var pending = round.ToList();
            while (pending.Count > 0)
            {
                var match = pending.FirstOrDefault(f => f.IsBye || Ready(f) <= next) ?? pending.OrderBy(Ready).First();
                Place(match); pending.Remove(match);
            }
        }
        if (next > start.Date.AddDays(1))
            throw new InvalidOperationException("The matches and rest breaks do not fit in one day. Choose an earlier start, a shorter match duration, or a multi-day schedule.");
        schedule.Fixtures = ordered;
    }

    public static Dictionary<string, IReadOnlyList<int>> DefaultPoolAssignments(IReadOnlyList<Team> teams, int poolCount)
    {
        if (poolCount < 2 || poolCount > teams.Count / 2) throw new InvalidOperationException("Each pool must contain at least two teams.");
        var ordered = teams.OrderBy(team => team.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var groups = Enumerable.Range(0, poolCount).ToDictionary(index => PoolLabel(index), _ => new List<int>());
        var capacity = (int)Math.Ceiling(ordered.Count / (double)poolCount);
        for (var index = 0; index < ordered.Count; index++) groups[PoolLabel(Math.Min(poolCount - 1, index / capacity))].Add(ordered[index].Id);
        return groups.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<int>)pair.Value);
    }

    private static string PoolLabel(int index) => index < 26 ? $"Pool {(char)('A' + index)}" : $"Pool {index + 1}";

    public static void RecordResult(FixtureSchedule schedule, FixtureRecord fixture, FixtureStatus status, string result, int? winnerTeamId)
    {
        if (fixture.IsBye) throw new InvalidOperationException("A bye cannot be edited as a match result.");
        if (status == FixtureStatus.Completed && !winnerTeamId.HasValue)
            throw new InvalidOperationException("Choose the winning team for a completed match.");
        if (winnerTeamId.HasValue && winnerTeamId != fixture.TeamAId && winnerTeamId != fixture.TeamBId)
            throw new InvalidOperationException("The selected winner must be one of the teams in this match.");
        fixture.Status = status;
        fixture.Result = result.Trim();
        fixture.WinnerTeamId = status == FixtureStatus.Completed ? winnerTeamId : null;
        if (schedule.Format == TournamentFormat.PoolPlay)
        {
            if (fixture.PoolName.Length > 0) RecalculatePoolQualifiers(schedule);
            else if (fixture.RoundName == "Semi-finals") RecalculateFinalists(schedule);
        }
        else if (schedule.Format == TournamentFormat.Knockout && fixture.Round < schedule.Fixtures.Max(item => item.Round))
        {
            ClearDownstream(schedule, fixture);
            ResolveBracketFixture(schedule, schedule.Fixtures.First(item => item.Round == fixture.Round + 1 && item.BracketIndex == fixture.BracketIndex / 2));
        }
    }

    private static void RecalculatePoolQualifiers(FixtureSchedule schedule)
    {
        var poolMatches = schedule.Fixtures.Where(fixture => fixture.PoolName.Length > 0).ToList();
        var semiFinals = schedule.Fixtures.Where(fixture => fixture.RoundName == "Semi-finals").OrderBy(fixture => fixture.BracketIndex).ToList();
        var finals = schedule.Fixtures.Where(fixture => fixture.RoundName is "Final" or "3rd place").ToList();
        if (poolMatches.Any(fixture => fixture.Status is not (FixtureStatus.Completed or FixtureStatus.Abandoned)))
        {
            foreach (var fixture in semiFinals.Concat(finals)) ResetQualifier(fixture);
            return;
        }

        var winnersByPool = GetPoolStandings(schedule).GroupBy(row => row.Pool).OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase).Select(group => group.ToList()).ToList();
        if (semiFinals.Count != 2 || winnersByPool.Count != 2) return;
        var poolA = winnersByPool[0]; var poolB = winnersByPool[1];
        if (poolA.Take(2).Concat(poolB.Take(2)).Any(row => row.Tied))
        {
            foreach (var fixture in semiFinals.Concat(finals)) ResetQualifier(fixture);
            return;
        }
        SetTeams(semiFinals[0], poolA[0].TeamId, poolB[1].TeamId);
        SetTeams(semiFinals[1], poolB[0].TeamId, poolA[1].TeamId);
        RecalculateFinalists(schedule);
    }

    public static List<PoolStanding> GetPoolStandings(FixtureSchedule schedule)
    {
        var rows = new List<PoolStanding>();
        foreach (var pool in schedule.Fixtures.Where(f => f.PoolName.Length > 0 && !f.IsBye).GroupBy(f => f.PoolName).OrderBy(g => g.Key))
        {
            var matches = pool.ToList();
            var complete = matches.All(f => f.Status is FixtureStatus.Completed or FixtureStatus.Abandoned);
            var records = matches.SelectMany(f => new[] { f.TeamAId, f.TeamBId }).OfType<int>().Distinct().Select(id =>
            {
                var played = matches.Where(f => (f.TeamAId == id || f.TeamBId == id) && f.Status is FixtureStatus.Completed or FixtureStatus.Abandoned).ToList();
                var wins = played.Count(f => f.Status == FixtureStatus.Completed && f.WinnerTeamId == id);
                var losses = played.Count(f => f.Status == FixtureStatus.Completed && f.WinnerTeamId.HasValue && f.WinnerTeamId != id);
                return new PoolStanding(pool.Key, id, 0, played.Count, wins, losses, wins * 2 + played.Count(f => f.Status == FixtureStatus.Abandoned), false, complete);
            }).ToList();
            var position = 1;
            foreach (var pointsGroup in records.GroupBy(r => r.Points).OrderByDescending(g => g.Key))
            {
                var ids = pointsGroup.Select(r => r.TeamId).ToHashSet();
                var meetings = matches.Where(f => f.TeamAId.HasValue && f.TeamBId.HasValue && ids.Contains(f.TeamAId.Value) && ids.Contains(f.TeamBId.Value)).ToList();
                var headToHeadReady = meetings.Count > 0 && meetings.All(f => f.Status is FixtureStatus.Completed or FixtureStatus.Abandoned);
                var ranked = pointsGroup.Select(row => (Row: row, Head: headToHeadReady ? meetings.Count(f => f.WinnerTeamId == row.TeamId && f.Status == FixtureStatus.Completed) * 2 + meetings.Count(f => f.Status == FixtureStatus.Abandoned && (f.TeamAId == row.TeamId || f.TeamBId == row.TeamId)) : 0));
                foreach (var tiedGroup in ranked.GroupBy(r => r.Head).OrderByDescending(g => g.Key))
                {
                    foreach (var item in tiedGroup.OrderBy(r => r.Row.TeamId)) rows.Add(item.Row with { Position = position, Tied = tiedGroup.Count() > 1 });
                    position += tiedGroup.Count();
                }
            }
        }
        return rows;
    }

    public static Dictionary<Guid, string> GetConflictWarnings(FixtureSchedule schedule, int? minimumRestMinutes = null)
    {
        var restMinutes = minimumRestMinutes ?? schedule.MinimumRestMinutes;
        var warnings = schedule.Fixtures.ToDictionary(f => f.Id, _ => new List<string>());
        var matches = schedule.Fixtures.Where(f => !f.IsBye && f.Status != FixtureStatus.Abandoned).OrderBy(f => f.ScheduledAt).ToList();
        for (var i = 0; i < matches.Count; i++)
        for (var j = i + 1; j < matches.Count; j++)
        {
            var a = matches[i]; var b = matches[j];
            var gap = (b.ScheduledAt - a.ScheduledAt).TotalMinutes - Math.Max(1, a.DurationMinutes);
            var sameTeam = new[] { a.TeamAId, a.TeamBId }.OfType<int>().Intersect(new[] { b.TeamAId, b.TeamBId }.OfType<int>()).Any();
            var reasons = new List<string>();
            if (gap < 0 && !string.IsNullOrWhiteSpace(a.Ground) && string.Equals(a.Ground.Trim(), b.Ground.Trim(), StringComparison.OrdinalIgnoreCase)) reasons.Add("Ground overlap");
            if (sameTeam && gap < 0) reasons.Add("Team overlap");
            else if (sameTeam && gap < restMinutes) reasons.Add($"Rest below {restMinutes} min");
            if (reasons.Count == 0) continue;
            warnings[a.Id].Add($"{string.Join(", ", reasons)} — match {b.MatchNumber}");
            warnings[b.Id].Add($"{string.Join(", ", reasons)} — match {a.MatchNumber}");
        }
        return warnings.ToDictionary(pair => pair.Key, pair => string.Join("; ", pair.Value));
    }

    private static void RecalculateFinalists(FixtureSchedule schedule)
    {
        var semiFinals = schedule.Fixtures.Where(fixture => fixture.RoundName == "Semi-finals").OrderBy(fixture => fixture.BracketIndex).ToList();
        if (semiFinals.Count != 2) return;
        var thirdPlace = schedule.Fixtures.FirstOrDefault(fixture => fixture.RoundName == "3rd place");
        var final = schedule.Fixtures.FirstOrDefault(fixture => fixture.RoundName == "Final");
        if (thirdPlace == null || final == null) return;
        if (semiFinals.Any(fixture => fixture.Status != FixtureStatus.Completed))
        {
            ResetQualifier(thirdPlace); ResetQualifier(final); return;
        }
        SetTeams(final, semiFinals[0].WinnerTeamId, semiFinals[1].WinnerTeamId);
        SetTeams(thirdPlace, LosingTeam(semiFinals[0]), LosingTeam(semiFinals[1]));
    }

    private static int? LosingTeam(FixtureRecord fixture) => fixture.WinnerTeamId == fixture.TeamAId ? fixture.TeamBId : fixture.TeamAId;

    private static void SetTeams(FixtureRecord fixture, int? teamA, int? teamB)
    {
        if (fixture.TeamAId == teamA && fixture.TeamBId == teamB) return;
        fixture.TeamAId = teamA; fixture.TeamBId = teamB;
        fixture.Status = FixtureStatus.Scheduled; fixture.Result = ""; fixture.WinnerTeamId = null;
    }

    private static void ResetQualifier(FixtureRecord fixture)
    {
        fixture.TeamAId = null; fixture.TeamBId = null;
        fixture.Status = FixtureStatus.Scheduled; fixture.Result = ""; fixture.WinnerTeamId = null;
    }

    private static void ClearDownstream(FixtureSchedule schedule, FixtureRecord source)
    {
        var index = source.BracketIndex / 2;
        var target = schedule.Fixtures.FirstOrDefault(item => item.Round == source.Round + 1 && item.BracketIndex == index);
        if (target == null) return;
        if (source.BracketIndex % 2 == 0) target.TeamAId = null; else target.TeamBId = null;
        target.Status = FixtureStatus.Scheduled;
        target.WinnerTeamId = null;
        target.Result = "";
        ClearDownstream(schedule, target);
        ResolveBracketFixture(schedule, target);
    }

    private static void AdvanceWinner(FixtureSchedule schedule, FixtureRecord source, int winnerId)
    {
        var targetIndex = source.BracketIndex / 2;
        var target = schedule.Fixtures.First(item => item.Round == source.Round + 1 && item.BracketIndex == targetIndex);
        if (source.BracketIndex % 2 == 0) target.TeamAId = winnerId; else target.TeamBId = winnerId;
        ResolveBracketFixture(schedule, target);
    }

    private static void AdvanceAutomaticByes(FixtureSchedule schedule)
    {
        foreach (var bye in schedule.Fixtures.Where(fixture => fixture.IsBye).ToList())
            if (bye.Round < schedule.Fixtures.Max(item => item.Round) && bye.WinnerTeamId.HasValue)
                AdvanceWinner(schedule, bye, bye.WinnerTeamId.Value);
    }

    private static void ResolveBracketFixture(FixtureSchedule schedule, FixtureRecord fixture)
    {
        var feeders = schedule.Fixtures.Where(item => item.Round == fixture.Round - 1 && item.BracketIndex / 2 == fixture.BracketIndex).ToList();
        if (feeders.Count < 2 || feeders.Any(item => item.Status != FixtureStatus.Completed)) return;
        var winners = feeders.Where(item => item.WinnerTeamId.HasValue).Select(item => item.WinnerTeamId!.Value).ToList();
        if (winners.Count == 1)
        {
            fixture.TeamAId = winners[0]; fixture.TeamBId = null;
            fixture.IsBye = true; fixture.Status = FixtureStatus.Completed; fixture.Result = "Bye"; fixture.WinnerTeamId = winners[0];
            if (fixture.Round < schedule.Fixtures.Max(item => item.Round)) AdvanceWinner(schedule, fixture, winners[0]);
        }
        else if (winners.Count == 2)
        {
            fixture.TeamAId = winners[0]; fixture.TeamBId = winners[1];
            fixture.IsBye = false; fixture.Status = FixtureStatus.Scheduled; fixture.Result = ""; fixture.WinnerTeamId = null;
        }
    }

    private static List<List<(int Home, int Away)>> BuildLeagueRounds(List<int> teamIds)
    {
        var rotation = teamIds.Select(id => (int?)id).ToList();
        if (rotation.Count % 2 != 0) rotation.Add(null);
        var rounds = new List<List<(int Home, int Away)>>();
        var count = rotation.Count;
        for (var round = 0; round < count - 1; round++)
        {
            var games = new List<(int Home, int Away)>();
            for (var index = 0; index < count / 2; index++)
            {
                var first = rotation[index]; var second = rotation[count - 1 - index];
                if (!first.HasValue || !second.HasValue) continue;
                var swap = (round + index) % 2 != 0;
                games.Add(swap ? (second.Value, first.Value) : (first.Value, second.Value));
            }
            rounds.Add(games);
            var last = rotation[^1]; rotation.RemoveAt(rotation.Count - 1); rotation.Insert(1, last);
        }
        return rounds;
    }

    private static string PickGround(IReadOnlyList<string> grounds, int index) => grounds.Count == 0 ? "" : grounds[index % grounds.Count];

    private static string RoundName(int roundCount, int round) => round == roundCount ? "Final"
        : round == roundCount - 1 ? "Semi-final"
        : round == roundCount - 2 ? "Quarter-final"
        : $"Knockout · Round {round}";

    private static void EnsureTable(AuctionDb db)
    {
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS TournamentFixtures (Id INTEGER PRIMARY KEY CHECK (Id = 1), Data TEXT NOT NULL)");
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
    }
}
