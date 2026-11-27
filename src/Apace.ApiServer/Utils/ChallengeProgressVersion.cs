using Apace.ApiServer.Controllers.EarthApi;

namespace Apace.ApiServer.Utils;

public sealed class ChallengeProgressVersion
{
    public long UpdatedAt { get; set; }
    public string? DailyDateUtc { get; set; }
    public string? ActiveSeasonId { get; set; }
    public string? ActiveSeasonChallengeId { get; set; }
    public int TappablesRedeemed { get; set; }
    public Dictionary<string, int> ObjectiveCounts { get; set; } = [];
    public HashSet<string> ClaimedChallengeIds { get; set; } = [];
    public HashSet<string> RemovedContinuousChallengeIds { get; set; } = [];
    public Dictionary<string, int> SeasonObjectiveCounts { get; set; } = [];
    public Dictionary<string, int> SeasonChallengeCounts { get; set; } = [];
    public HashSet<string> SeasonEventFlags { get; set; } = [];
    public long SeasonStartedAt { get; set; }

    public long GetSeasonEndsAt()
        => SeasonStartedAt + (long)ChallengesController.SeasonLength.TotalMilliseconds;

    private void EnsureSeason(long timestamp)
    {
        long length = (long)ChallengesController.SeasonLength.TotalMilliseconds;

        if (SeasonStartedAt <= 0)
        {
            SeasonStartedAt = new DateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
            return;
        }

        if (timestamp < SeasonStartedAt + length)
        {
            return;
        }

        SeasonStartedAt += (timestamp - SeasonStartedAt) / length * length;

        SeasonObjectiveCounts = [];
        SeasonChallengeCounts = [];
        SeasonEventFlags = [];
        ClaimedChallengeIds.ExceptWith(ChallengesController.SeasonChallengeIds());
        ActiveSeasonChallengeId = null;
    }

    public void EnsureDate(long timestamp)
    {
        string today = DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
            .UtcDateTime
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        ObjectiveCounts ??= [];
        SeasonObjectiveCounts ??= [];
        SeasonChallengeCounts ??= [];
        SeasonEventFlags ??= [];
        ClaimedChallengeIds ??= [];
        RemovedContinuousChallengeIds ??= [];

        EnsureSeason(timestamp);

        if (DailyDateUtc == today)
        {
            return;
        }

        DailyDateUtc = today;
        TappablesRedeemed = 0;
        ObjectiveCounts = [];
        RemovedContinuousChallengeIds = [];
        ClaimedChallengeIds.ExceptWith(ChallengesController.DailyChallengeKeys);
        ClaimedChallengeIds.ExceptWith(ChallengesController.ContinuousChallengeKeys);
        ClaimedChallengeIds.ExceptWith(ChallengesController.TimedSeasonListChallengeIds());
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

    public void AddSeasonChallengeProgress(long timestamp, string challengeId, int amount = 1)
    {
        EnsureDate(timestamp);
        UpdatedAt = timestamp;
        SeasonChallengeCounts[challengeId] = SeasonChallengeCounts.GetValueOrDefault(challengeId) + amount;
    }

    public int GetSeasonChallengeProgress(string challengeId)
        => SeasonChallengeCounts?.GetValueOrDefault(challengeId) ?? 0;

    public int GetObjectiveProgress(string objectiveId)
        => ObjectiveCounts?.GetValueOrDefault(objectiveId) ?? 0;
}
