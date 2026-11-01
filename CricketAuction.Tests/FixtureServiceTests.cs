using CricketAuction.Domain;
using CricketAuction.Infrastructure;

namespace CricketAuction.Tests;

public sealed class FixtureServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CricketAuctionFixtureTests", Guid.NewGuid().ToString("N"));
    private readonly Workspace workspace;

    public FixtureServiceTests()
    {
        workspace = new Workspace(root);
        workspace.Create("Fixture Cup", "Fixture Cup Auction");
    }

    [Fact]
    public void LeagueScheduleCreatesEveryPairOnceAndSupportsReturnLegs()
    {
        var teams = Enumerable.Range(1, 4).Select(index => new Team { Id = index, Name = $"Team {index}" }).ToList();
        var oneLeg = FixtureService.Generate(teams, TournamentFormat.League, new DateTime(2026, 11, 1), new TimeSpan(9, 0, 0), 2, ["Ground A", "Ground B"], false);
        Assert.Equal(6, oneLeg.Fixtures.Count);
        Assert.Equal(3, oneLeg.Fixtures.Select(fixture => fixture.Round).Distinct().Count());
        Assert.Equal(6, oneLeg.Fixtures.Select(fixture => (Math.Min(fixture.TeamAId!.Value, fixture.TeamBId!.Value), Math.Max(fixture.TeamAId.Value, fixture.TeamBId!.Value))).Distinct().Count());
        Assert.All(oneLeg.Fixtures, fixture => Assert.Contains(fixture.Ground, new[] { "Ground A", "Ground B" }));

        var twoLeg = FixtureService.Generate(teams, TournamentFormat.League, new DateTime(2026, 11, 1), new TimeSpan(9, 0, 0), 2, [], true);
        Assert.Equal(12, twoLeg.Fixtures.Count);
        Assert.All(oneLeg.Fixtures, fixture => Assert.Contains(twoLeg.Fixtures, returnLeg => returnLeg.TeamAId == fixture.TeamBId && returnLeg.TeamBId == fixture.TeamAId));
    }

    [Fact]
    public void KnockoutBracketMarksByesAndAdvancesWinners()
    {
        var teams = Enumerable.Range(1, 5).Select(index => new Team { Id = index, Name = $"Team {index}" }).ToList();
        var schedule = FixtureService.Generate(teams, TournamentFormat.Knockout, new DateTime(2026, 11, 1), new TimeSpan(9, 0, 0), 3, [], false);
        Assert.Equal(7, schedule.Fixtures.Count);
        Assert.Equal(3, schedule.Fixtures.Count(fixture => fixture.Round == 1 && fixture.IsBye));
        var firstRoundMatch = Assert.Single(schedule.Fixtures.Where(fixture => fixture.Round == 1 && !fixture.IsBye));
        FixtureService.RecordResult(schedule, firstRoundMatch, FixtureStatus.Completed, "Won by 5 wickets", firstRoundMatch.TeamAId);
        var semiFinals = schedule.Fixtures.Where(fixture => fixture.Round == 2).ToList();
        Assert.Equal(2, semiFinals.Count);
        Assert.All(semiFinals, fixture => Assert.NotNull(fixture.TeamAId));
        Assert.All(semiFinals, fixture => Assert.NotNull(fixture.TeamBId));
        foreach (var semiFinal in semiFinals)
            FixtureService.RecordResult(schedule, semiFinal, FixtureStatus.Completed, "Won by 10 runs", semiFinal.TeamBId);
        var final = Assert.Single(schedule.Fixtures.Where(fixture => fixture.Round == 3));
        Assert.NotNull(final.TeamAId);
        Assert.NotNull(final.TeamBId);
    }

    [Fact]
    public void FixtureSchedulePersistsInsideItsAuctionDatabase()
    {
        var fixtureService = new FixtureService(workspace);
        var teams = new List<Team> { new() { Id = 1, Name = "Team A" }, new() { Id = 2, Name = "Team B" } };
        var schedule = FixtureService.Generate(teams, TournamentFormat.League, DateTime.Today, new TimeSpan(10, 0, 0), 1, ["Main Ground"], false);
        fixtureService.Save(schedule);
        var reloaded = new FixtureService(new Workspace(root)).Load();
        var fixture = Assert.Single(reloaded.Fixtures);
        Assert.Equal(TournamentFormat.League, reloaded.Format);
        Assert.Equal("Main Ground", fixture.Ground);
        Assert.Equal(teams[0].Id, fixture.TeamAId);
    }

    [Fact]
    public void CompletingFixtureWithWinnerPersistsStatusAndWinner()
    {
        var fixtureService = new FixtureService(workspace);
        var teams = new List<Team> { new() { Id = 1, Name = "Team A" }, new() { Id = 2, Name = "Team B" } };
        var schedule = FixtureService.Generate(teams, TournamentFormat.League, DateTime.Today, new TimeSpan(10, 0, 0), 1, [], false);
        var fixture = Assert.Single(schedule.Fixtures);
        FixtureService.RecordResult(schedule, fixture, FixtureStatus.Completed, "Won by 4 wickets", fixture.TeamAId);
        fixtureService.Save(schedule);

        var saved = Assert.Single(fixtureService.Load().Fixtures);
        Assert.Equal(FixtureStatus.Completed, saved.Status);
        Assert.Equal(fixture.TeamAId, saved.WinnerTeamId);
        Assert.Equal("Won by 4 wickets", saved.Result);
    }

    [Fact]
    public void SixTeamPoolsGenerateTenFixturesAndAdvanceTopTwoToCrossSemifinals()
    {
        var teams = Enumerable.Range(1, 6).Select(index => new Team { Id = index, Name = $"Team {index}" }).ToList();
        var schedule = FixtureService.Generate(teams, TournamentFormat.PoolPlay, new DateTime(2026, 11, 1), new TimeSpan(9, 0, 0), 1, ["Main Ground"], false);

        Assert.Equal(10, schedule.Fixtures.Count);
        Assert.Equal(3, schedule.Fixtures.Count(fixture => fixture.PoolName == "Pool A"));
        Assert.Equal(3, schedule.Fixtures.Count(fixture => fixture.PoolName == "Pool B"));
        var semis = schedule.Fixtures.Where(fixture => fixture.RoundName == "Semi-finals").OrderBy(fixture => fixture.BracketIndex).ToList();
        Assert.Equal("Pool A 1st", semis[0].TeamASeed);
        Assert.Equal("Pool B 2nd", semis[0].TeamBSeed);
        Assert.Equal("Pool B 1st", semis[1].TeamASeed);
        Assert.Equal("Pool A 2nd", semis[1].TeamBSeed);
        Assert.Contains(schedule.Fixtures, fixture => fixture.RoundName == "3rd place");
        Assert.Contains(schedule.Fixtures, fixture => fixture.RoundName == "Final");

        foreach (var poolFixture in schedule.Fixtures.Where(fixture => fixture.PoolName.Length > 0))
            FixtureService.RecordResult(schedule, poolFixture, FixtureStatus.Completed, "Won", Math.Min(poolFixture.TeamAId!.Value, poolFixture.TeamBId!.Value));
        Assert.Equal(1, semis[0].TeamAId);
        Assert.Equal(5, semis[0].TeamBId);
        Assert.Equal(4, semis[1].TeamAId);
        Assert.Equal(2, semis[1].TeamBId);

        FixtureService.RecordResult(schedule, semis[0], FixtureStatus.Completed, "Won", semis[0].TeamAId);
        FixtureService.RecordResult(schedule, semis[1], FixtureStatus.Completed, "Won", semis[1].TeamBId);
        Assert.Equal(1, schedule.Fixtures.Single(fixture => fixture.RoundName == "Final").TeamAId);
        Assert.Equal(2, schedule.Fixtures.Single(fixture => fixture.RoundName == "Final").TeamBId);
        Assert.Equal(5, schedule.Fixtures.Single(fixture => fixture.RoundName == "3rd place").TeamAId);
        Assert.Equal(4, schedule.Fixtures.Single(fixture => fixture.RoundName == "3rd place").TeamBId);
    }

    [Fact]
    public void PoolScheduleUsesExplicitTeamAssignmentsAndSupportsMoreThanTwoPools()
    {
        var teams = Enumerable.Range(1, 6).Select(index => new Team { Id = index, Name = $"Team {index}" }).ToList();
        IReadOnlyDictionary<string, IReadOnlyList<int>> assignments = new Dictionary<string, IReadOnlyList<int>>
        {
            ["Pool A"] = [1, 4, 6],
            ["Pool B"] = [2, 3, 5]
        };
        var schedule = FixtureService.Generate(teams, TournamentFormat.PoolPlay, new DateTime(2026, 11, 1), new TimeSpan(9, 0, 0), 1, [], false, assignments);
        Assert.All(schedule.Fixtures.Where(fixture => fixture.PoolName == "Pool A"), fixture => Assert.Contains(fixture.TeamAId!.Value, assignments["Pool A"]));
        Assert.All(schedule.Fixtures.Where(fixture => fixture.PoolName == "Pool A"), fixture => Assert.Contains(fixture.TeamBId!.Value, assignments["Pool A"]));

        var threePools = FixtureService.DefaultPoolAssignments(teams, 3);
        var poolStage = FixtureService.Generate(teams, TournamentFormat.PoolPlay, new DateTime(2026, 11, 1), new TimeSpan(9, 0, 0), 1, [], false, threePools);
        Assert.Equal(3, poolStage.Fixtures.Select(fixture => fixture.PoolName).Distinct().Count());
        Assert.Empty(poolStage.Fixtures.Where(fixture => fixture.PoolName.Length == 0));
    }

    [Fact]
    public void SavingNewScheduleReplacesExistingFixturesForThatTournament()
    {
        var fixtureService = new FixtureService(workspace);
        var teams = Enumerable.Range(1, 6).Select(index => new Team { Id = index, Name = $"Team {index}" }).ToList();
        fixtureService.Save(FixtureService.Generate(teams, TournamentFormat.League, DateTime.Today, new TimeSpan(9, 0, 0), 1, [], false));
        var replacement = FixtureService.Generate(teams, TournamentFormat.PoolPlay, DateTime.Today, new TimeSpan(9, 0, 0), 1, [], false);
        fixtureService.Save(replacement);

        var saved = fixtureService.Load();
        Assert.Equal(TournamentFormat.PoolPlay, saved.Format);
        Assert.Equal(10, saved.Fixtures.Count);
        Assert.DoesNotContain(saved.Fixtures, fixture => fixture.PoolName.Length == 0 && fixture.RoundName.StartsWith("League", StringComparison.Ordinal));
    }

    [Fact]
    public void CircularPoolTieDoesNotAdvanceTeamsBySeedOrder()
    {
        var teams = Enumerable.Range(1, 6).Select(id => new Team { Id = id, Name = $"Team {id}" }).ToList();
        var schedule = FixtureService.Generate(teams, TournamentFormat.PoolPlay, DateTime.Today, TimeSpan.FromHours(9), 1, [], false);
        foreach (var match in schedule.Fixtures.Where(f => f.PoolName.Length > 0))
        {
            var low = Math.Min(match.TeamAId!.Value, match.TeamBId!.Value);
            var high = Math.Max(match.TeamAId.Value, match.TeamBId.Value);
            FixtureService.RecordResult(schedule, match, FixtureStatus.Completed, "Won", high - low == 2 ? high : low);
        }
        Assert.All(FixtureService.GetPoolStandings(schedule), row => { Assert.True(row.Tied); Assert.Equal(2, row.Points); Assert.Equal(2, row.Played); });
        Assert.All(schedule.Fixtures.Where(f => f.RoundName == "Semi-finals"), match => { Assert.Null(match.TeamAId); Assert.Null(match.TeamBId); });
    }

    [Fact]
    public void HeadToHeadBreaksEqualPointsAndAbandonedMatchesAwardOnePoint()
    {
        var schedule = new FixtureSchedule { Fixtures =
        [
            new() { PoolName = "A", TeamAId = 1, TeamBId = 2, Status = FixtureStatus.Completed, WinnerTeamId = 1 },
            new() { PoolName = "A", TeamAId = 2, TeamBId = 3, Status = FixtureStatus.Completed, WinnerTeamId = 2 },
            new() { PoolName = "A", TeamAId = 1, TeamBId = 3, Status = FixtureStatus.Scheduled },
        ] };
        var rows = FixtureService.GetPoolStandings(schedule);
        Assert.Equal(1, rows[0].TeamId); Assert.False(rows[0].Tied);
        Assert.Equal(2, rows[1].TeamId); Assert.Equal(2, rows[1].Points);
        schedule.Fixtures[2].Status = FixtureStatus.Abandoned;
        rows = FixtureService.GetPoolStandings(schedule);
        Assert.Equal(3, rows[0].Points); Assert.Equal(1, rows[2].Points);
    }

    [Fact]
    public void ConflictChecksRespectDurationGroundsRestAndUnknownTeams()
    {
        var first = new FixtureRecord { MatchNumber = 1, TeamAId = 1, TeamBId = 2, Ground = " Main ", ScheduledAt = DateTime.Today.AddHours(9), DurationMinutes = 120 };
        var second = new FixtureRecord { MatchNumber = 2, TeamAId = 1, TeamBId = 3, Ground = "main", ScheduledAt = DateTime.Today.AddHours(10) };
        var schedule = new FixtureSchedule { Fixtures = [first, second] };
        var warnings = FixtureService.GetConflictWarnings(schedule);
        Assert.Contains("Ground overlap", warnings[first.Id]); Assert.Contains("Team overlap", warnings[second.Id]);
        second.ScheduledAt = DateTime.Today.AddHours(11);
        Assert.Contains("Rest below", FixtureService.GetConflictWarnings(schedule)[first.Id]);
        second.ScheduledAt = DateTime.Today.AddHours(12);
        Assert.All(FixtureService.GetConflictWarnings(schedule).Values, value => Assert.Empty(value));
        first.TeamAId = null; first.TeamBId = null; second.TeamAId = null; second.TeamBId = null;
        first.Ground = ""; second.Ground = ""; second.ScheduledAt = first.ScheduledAt;
        Assert.All(FixtureService.GetConflictWarnings(schedule).Values, value => Assert.Empty(value));
    }

    [Fact]
    public void ReloadPreservesCompletedPlayoffsAndDuration()
    {
        var teams = Enumerable.Range(1, 4).Select(id => new Team { Id = id, Name = $"Team {id}" }).ToList();
        var schedule = FixtureService.Generate(teams, TournamentFormat.PoolPlay, DateTime.Today, TimeSpan.FromHours(9), 1, [], false);
        foreach (var match in schedule.Fixtures.Where(f => f.PoolName.Length > 0 || f.RoundName == "Semi-finals"))
            FixtureService.RecordResult(schedule, match, FixtureStatus.Completed, "Won", match.TeamAId);
        var final = schedule.Fixtures.Single(f => f.RoundName == "Final");
        FixtureService.RecordResult(schedule, final, FixtureStatus.Completed, "Champion", final.TeamAId);
        final.DurationMinutes = 90;
        var service = new FixtureService(workspace); service.Save(schedule);
        var loaded = service.Load().Fixtures.Single(f => f.RoundName == "Final");
        Assert.Equal(FixtureStatus.Completed, loaded.Status); Assert.Equal(final.WinnerTeamId, loaded.WinnerTeamId); Assert.Equal(90, loaded.DurationMinutes);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void SingleDayAlternatesPoolsAndNumbersMatchesInPlayingOrder(int teamCount)
    {
        var teams = Enumerable.Range(1, teamCount).Select(id => new Team { Id = id, Name = $"Team {id}" }).ToList();
        var date = new DateTime(2026, 11, 1);
        var schedule = FixtureService.Generate(teams, TournamentFormat.PoolPlay, date, TimeSpan.FromHours(8), 0, ["Main"], false);
        var poolMatches = schedule.Fixtures.Where(f => f.PoolName.Length > 0).ToList();
        for (var index = 0; index < poolMatches.Count; index++)
        {
            Assert.Equal(index % 2 == 0 ? "Pool A" : "Pool B", poolMatches[index].PoolName);
            Assert.Equal(date.AddHours(8).AddMinutes(index * 40), poolMatches[index].ScheduledAt);
        }
        for (var index = 0; index < schedule.Fixtures.Count; index++)
        {
            Assert.Equal(index + 1, schedule.Fixtures[index].MatchNumber);
            Assert.Equal(40, schedule.Fixtures[index].DurationMinutes);
            Assert.Equal(date, schedule.Fixtures[index].ScheduledAt.Date);
        }
        // Resolve qualifiers to verify that reserved playoff breaks protect actual teams too.
        foreach (var match in schedule.Fixtures.Where(f => f.PoolName.Length > 0 || f.RoundName == "Semi-finals"))
            FixtureService.RecordResult(schedule, match, FixtureStatus.Completed, "Won", Math.Min(match.TeamAId!.Value, match.TeamBId!.Value));
        Assert.All(FixtureService.GetConflictWarnings(schedule).Values, warning => Assert.Empty(warning));
    }

    [Fact]
    public void UnequalPoolsAndLeagueAddRestWhenAlternationIsNotPossible()
    {
        var teams = Enumerable.Range(1, 5).Select(id => new Team { Id = id, Name = $"Team {id}" }).ToList();
        var schedule = FixtureService.Generate(teams, TournamentFormat.PoolPlay, DateTime.Today, TimeSpan.FromHours(8), 0, [], false);
        Assert.All(FixtureService.GetConflictWarnings(schedule).Values, warning => Assert.Empty(warning));
        var league = FixtureService.Generate(teams.Take(3).ToList(), TournamentFormat.League, DateTime.Today, TimeSpan.FromHours(8), 0, [], false);
        Assert.Equal(80, (league.Fixtures[1].ScheduledAt - league.Fixtures[0].ScheduledAt).TotalMinutes);
        Assert.All(FixtureService.GetConflictWarnings(league).Values, warning => Assert.Empty(warning));
    }

    [Fact]
    public void SingleDayRejectsScheduleThatWouldFinishTomorrow()
    {
        var teams = Enumerable.Range(1, 6).Select(id => new Team { Id = id, Name = $"Team {id}" }).ToList();
        Assert.Throws<InvalidOperationException>(() => FixtureService.Generate(teams, TournamentFormat.PoolPlay, DateTime.Today, TimeSpan.FromHours(22), 0, [], false));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
