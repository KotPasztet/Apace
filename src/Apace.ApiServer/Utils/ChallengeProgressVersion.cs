namespace Apace.ApiServer.Utils;

public sealed class ChallengeProgressVersion
{
    public long UpdatedAt { get; set; }
    public string? DailyDateUtc { get; set; }
    public string? ActiveSeasonId { get; set; }
    public string? ActiveSeasonChallengeId { get; set; }
    public int TappablesRedeemed { get; set; }
    public Dictionary<string, int> ObjectiveCounts { get; set; } = [];
    // season challenge progress: unlike ObjectiveCounts this is never reset at the start of a new day
    public Dictionary<string, int> SeasonObjectiveCounts { get; set; } = [];
    public HashSet<string> ClaimedChallengeIds { get; set; } = [];
    public HashSet<string> RemovedContinuousChallengeIds { get; set; } = [];
    // when the player's current run of the season started (UTC midnight, 0 = not started yet)
    public long SeasonStartedAt { get; set; }

    /// <summary>
    /// When the player's current run of the season ends.
    /// </summary>
    public long GetSeasonEndsAt()
        => SeasonStartedAt + (long)Apace.ApiServer.Controllers.EarthApi.ChallengesController.SeasonLength.TotalMilliseconds;

    /// <summary>
    /// Starts the season for a player who has no start date yet, and restarts it (clearing the season progress and claims,
    /// so every reward can be earned again) once the run has ended.
    /// </summary>
    private void EnsureSeason(long timestamp)
    {
        long length = (long)Apace.ApiServer.Controllers.EarthApi.ChallengesController.SeasonLength.TotalMilliseconds;

        if (SeasonStartedAt <= 0)
        {
            SeasonStartedAt = new DateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
            return;
        }

        if (timestamp < SeasonStartedAt + length)
        {
            return;
        }

        // the new run starts exactly where the old one ended (several runs may have passed while the player was away)
        SeasonStartedAt += (timestamp - SeasonStartedAt) / length * length;

        SeasonObjectiveCounts = [];
        ClaimedChallengeIds.RemoveWhere(Apace.ApiServer.Controllers.EarthApi.ChallengesController.SeasonChallengeIds().ToHashSet().Contains);
        ActiveSeasonChallengeId = null;
    }

    public void EnsureDate(long timestamp)
    {
        string today = DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
            .UtcDateTime
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        ObjectiveCounts ??= [];
        SeasonObjectiveCounts ??= [];
        ClaimedChallengeIds ??= [];
        RemovedContinuousChallengeIds ??= [];

        EnsureSeason(timestamp);

        if (DailyDateUtc == today)
        {
            return;
        }

        // the claimed ids of daily-scoped challenges are only valid for the day they were claimed on, otherwise the
        // daily group and every daily/continuous challenge would stay hidden (and never notify) after their first completion
        foreach (string challengeId in Apace.ApiServer.Controllers.EarthApi.ChallengesController.DailyScopedChallengeIds())
        {
            ClaimedChallengeIds.Remove(challengeId);
        }

        DailyDateUtc = today;
        TappablesRedeemed = 0;
        ObjectiveCounts = [];
        RemovedContinuousChallengeIds = [];
    }

    public int RecordTappable(long timestamp)
    {
        EnsureDate(timestamp);
        UpdatedAt = timestamp;
        TappablesRedeemed++;
        return TappablesRedeemed;
    }

    public void AddObjectiveProgress(long timestamp, string objectiveId, int amount = 1)
    {
        EnsureDate(timestamp);
        UpdatedAt = timestamp;
        ObjectiveCounts ??= [];
        ObjectiveCounts[objectiveId] = ObjectiveCounts.GetValueOrDefault(objectiveId) + amount;
    }

    public void AddSeasonObjectiveProgress(long timestamp, string objectiveId, int amount = 1)
    {
        EnsureDate(timestamp);
        UpdatedAt = timestamp;
        SeasonObjectiveCounts ??= [];
        SeasonObjectiveCounts[objectiveId] = SeasonObjectiveCounts.GetValueOrDefault(objectiveId) + amount;
    }

    public int GetObjectiveProgress(string objectiveId)
    {
        ObjectiveCounts ??= [];
        SeasonObjectiveCounts ??= [];
        return Math.Max(ObjectiveCounts.GetValueOrDefault(objectiveId), SeasonObjectiveCounts.GetValueOrDefault(objectiveId));
    }
}
