namespace CricketAuction.Domain;

public static class PresentationOrder
{
    public static List<Player> Create(IEnumerable<Player> players, Random? random = null)
    {
        var shuffled = players.Where(p => p.Level != 0).ToArray();
        (random ?? Random.Shared).Shuffle(shuffled);
        // Stable sorting retains the shuffled order inside each level.
        return shuffled.OrderBy(p => p.Level.HasValue ? 0 : 1)
            .ThenBy(p => p.Level).ToList();
    }
}
