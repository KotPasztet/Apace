using System.Text.Json.Nodes;
using Apace.ApiServer.Controllers.EarthApi;
using Apace.ApiServer.Utils;
using Apace.Buildplate.Launcher;
using Apace.DB.Models.Player;
using static Apace.ApiServer.Utils.BuildplateInstancesManager;

namespace Apace.Tests;

public sealed class ChallengeEventPipelineTests
{
    private const string PlayerId = "6bd8769afeba7d52";
    private const string SuperPrizesId = "96593763-a8a6-41f8-986a-8d6df0470c57";
    private const string Prefix = "[18:02:11] [Server thread/INFO]: [Not Secure] ";

    private static long At(string utc)
        => DateTimeOffset.Parse(utc, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();

    private static JsonObject Data(PersistentProcessManager.ChallengeLine line)
        => JsonNode.Parse(line.Data)!.AsObject();

    [Fact]
    public void AnEventLineNamesThePlayerAndTheEvent()
    {
        var line = PersistentProcessManager.ParseChallengeLine($"{Prefix}[{PlayerId}] [apace-event] gold_ore\r")!;

        Assert.Equal("challengeEvent", line.EventType);
        Assert.Equal(PlayerId, line.PlayerId);
        Assert.Equal(PlayerId, Data(line)["playerId"]!.GetValue<string>());
        Assert.Equal("gold_ore", Data(line)["event"]!.GetValue<string>());
    }

    [Fact]
    public void AnimalLinesAreRecognised()
    {
        var chicken = PersistentProcessManager.ParseChallengeLine($"{Prefix}[{PlayerId}] [apace-placed-chicken]")!;
        var cow = PersistentProcessManager.ParseChallengeLine($"[{PlayerId}] [apace-placed-cow]")!;

        Assert.Equal("mobPlaced", chicken.EventType);
        Assert.Equal("chicken", Data(chicken)["mob"]!.GetValue<string>());
        Assert.Equal("cow", Data(cow)["mob"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("[18:02:11] [Server thread/INFO]: <someone> [apace-event] gold_ore")]
    [InlineData("[18:02:11] [Server thread/INFO]: [Not Secure] <someone> [6bd8769afeba7d52] [apace-event] gold_ore")]
    [InlineData("[18:02:11] [Server thread/INFO]: <someone> [apace-placed-cow]")]
    [InlineData("[18:02:11] [Server thread/INFO]: [Not Secure] [6bd8769afeba7d52] [apace-event] Gold_Ore")]
    [InlineData("[18:02:11] [Server thread/INFO]: [Not Secure] [6bd8769afeba7d52] [apace-event] gold ore")]
    [InlineData("[18:02:11] [Server thread/INFO]: [Not Secure] [6bd8769afeba7d52] [apace-event] ")]
    [InlineData("[18:02:11] [Server thread/INFO]: [Not Secure] [6bd8769a:feba] [apace-event] gold_ore")]
    [InlineData("[18:02:11] [Server thread/INFO]: [apace-event] gold_ore")]
    [InlineData("[18:02:11] [Server thread/INFO]: Done (3.2s)! For help, type \"help\"")]
    public void OtherLinesAreIgnored(string line)
        => Assert.Null(PersistentProcessManager.ParseChallengeLine(line));

    [Theory]
    [InlineData("spider_sword", "8e052786-0495-4eb9-80bf-e1b36b9d89ef", 2, false, false, false)]
    [InlineData("spider_kill", "a1394f77-20ba-44c2-b46c-e8d7933f2e51", 3, false, false, false)]
    [InlineData("gold_ore", "8ff3a6c3-0a1a-4343-8672-7f559cc8918d", 8, false, false, true)]
    [InlineData("oak_log", "761c6754-ad1f-4cbf-a116-8a1e1dd611b9", 8, false, false, true)]
    [InlineData("creeper_flint", "68337eb8-c1c8-4f57-876a-d262ca7a22f4", 1, true, false, true)]
    [InlineData("collect_salmon", "29ebe650-072f-4f70-996f-4ffdda93ed1f", 2, false, true, false)]
    [InlineData("placed_glow_squid", "abd271f8-22cd-42ff-b985-cd5e642ea25a", 2, true, true, false)]
    [InlineData("placed_parrot", "2bb26b41-e841-4815-ade5-a0d93ac51258", 3, true, true, false)]
    [InlineData("placed_uncommon", "d434c853-7fab-4ac7-b64b-80fb6dd9ddd9", 1, true, true, false)]
    [InlineData("placed_rare", "2ebbf878-5c69-4f55-8858-83bd53c57381", 2, false, true, false)]
    [InlineData("hostile_tnt", "dbdb4592-4211-4ffe-a76c-8a3a2213c93c", 7, false, false, false)]
    [InlineData("hostile_gold_pickaxe", "fb13127e-d19d-48bd-a977-f830eecff180", 4, false, false, false)]
    [InlineData("dark_oak_slab", "3183cf3d-19e5-4bf5-b8a8-3085f4e650d3", 6, false, false, false)]
    [InlineData("terracotta_light_blue", "63e5d65e-1152-4adc-97e7-2818854a867a", 3, true, false, false)]
    [InlineData("glazed_terracotta_black", "63e5d65e-1152-4adc-97e7-2818854a867a", 3, true, false, false)]
    [InlineData("sapling_dark_oak", "d8d2abd5-f318-4c34-a257-57b9691a2774", 2, true, true, false)]
    [InlineData("leaves_birch", "242b6706-8112-4302-819b-bde98fd574f6", 2, true, false, false)]
    [InlineData("adventure_rare_chest", "ab2e0734-62b1-4383-a239-d1b4d4a93dc4", 1, false, false, true)]
    public void EventRulesPointAtTheirSeasonChallenge(string eventName, string challengeId, int threshold, bool unique, bool ownBuildplate, bool adventure)
    {
        ChallengeEvents.EventRule rule = ChallengeEvents.GetEventRule(eventName)!;

        Assert.Equal(challengeId, rule.ChallengeId);
        Assert.Equal(ChallengesController.GetSeasonChallengeReferenceId(challengeId), rule.ReferenceId);
        Assert.Equal(ChallengesController.GetSeasonChallengeThreshold(challengeId), rule.Threshold);
        Assert.Equal(threshold, rule.Threshold);
        Assert.Equal(unique, rule.Unique);
        Assert.Equal(ownBuildplate, rule.RequiresOwnBuildplate);
        Assert.Equal(adventure, rule.RequiresAdventure);
    }

    [Theory]
    [InlineData("terracotta_mauve")]
    [InlineData("sapling_cherry")]
    [InlineData("leaves_")]
    [InlineData("placed_chicken")]
    [InlineData("nonsense")]
    public void UnknownEventsHaveNoRule(string eventName)
        => Assert.Null(ChallengeEvents.GetEventRule(eventName));

    private static InstanceInfo Instance(InstanceType type, string? playerId)
        => new(type, Guid.NewGuid().ToString(), playerId, null, "plate", "127.0.0.1", 19132, true, false);

    [Fact]
    public void BuildplateOnlyEventsNeedThePlayersOwnBuildplate()
    {
        var rule = ChallengeEvents.GetEventRule("placed_parrot")!;

        Assert.True(ShouldCountChallengeEvent([Instance(InstanceType.BUILD, PlayerId)], PlayerId, rule));
        Assert.False(ShouldCountChallengeEvent([Instance(InstanceType.BUILD, "someone-else")], PlayerId, rule));
        Assert.False(ShouldCountChallengeEvent([Instance(InstanceType.PLAYER_ADVENTURE, PlayerId)], PlayerId, rule));
        Assert.False(ShouldCountChallengeEvent([], PlayerId, rule));
    }

    [Fact]
    public void AdventureOnlyEventsNeedThePlayerInAnAdventure()
    {
        var rule = ChallengeEvents.GetEventRule("gold_ore")!;

        Assert.True(ShouldCountChallengeEvent([Instance(InstanceType.PLAYER_ADVENTURE, PlayerId)], PlayerId, rule));
        Assert.True(ShouldCountChallengeEvent([Instance(InstanceType.ENCOUNTER, PlayerId)], PlayerId, rule));
        Assert.False(ShouldCountChallengeEvent([Instance(InstanceType.ENCOUNTER, null)], PlayerId, rule));
        Assert.False(ShouldCountChallengeEvent([Instance(InstanceType.BUILD, PlayerId)], PlayerId, rule));
    }

    [Fact]
    public void UngatedEventsCountAnywhere()
        => Assert.True(ShouldCountChallengeEvent([], PlayerId, ChallengeEvents.GetEventRule("spider_kill")!));

    private static ChallengeProgressVersion ActiveOn(params string[] challengeIds)
    {
        var progress = new ChallengeProgressVersion();
        progress.ClaimedChallengeIds.UnionWith(ChallengesController.SeasonChallengeIds().Where(id => !challengeIds.Contains(id)));
        progress.ActiveSeasonChallengeId = challengeIds[0];
        return progress;
    }

    [Fact]
    public void ALockedChallengeDoesNotCountEvenWhenSelected()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        var tokens = new Tokens();
        var rule = ChallengeEvents.GetEventRule("sapling_oak")!;
        progress.ActiveSeasonChallengeId = rule.ChallengeId;
        Assert.False(ChallengesController.GetSeasonChallengeStatus(progress, rule.ChallengeId).Unlocked);

        Assert.False(ChallengeEvents.ApplyChallengeEvent(progress, tokens, now, "sapling_oak", rule));

        Assert.Equal(0, progress.GetSeasonChallengeProgress(rule.ChallengeId));
        Assert.Empty(progress.SeasonEventFlags);
        Assert.Empty(tokens.GetTokens());
    }

    [Fact]
    public void AUniqueEventCountsOnce()
    {
        long now = At("2026-11-25T12:00:00Z");
        var tokens = new Tokens();
        var rule = ChallengeEvents.GetEventRule("sapling_oak")!;
        var progress = ActiveOn(rule.ChallengeId);

        Assert.True(ChallengeEvents.ApplyChallengeEvent(progress, tokens, now, "sapling_oak", rule));
        Assert.False(ChallengeEvents.ApplyChallengeEvent(progress, tokens, now, "sapling_oak", rule));
        Assert.True(ChallengeEvents.ApplyChallengeEvent(progress, tokens, now, "sapling_birch", rule));

        Assert.Equal(2, progress.GetSeasonChallengeProgress(rule.ChallengeId));
        Assert.Equal(2, tokens.GetTokens().Length);
        Assert.IsType<Tokens.ChallengeCompletedToken>(tokens.GetTokens().Last().Token);
    }

    [Fact]
    public void AChickenAndACowCompleteThePlaceAnimalsChallenge()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion { ActiveSeasonChallengeId = "1ccbe15e-3a72-4d8b-ad0b-00a23ac72eb1" };
        var tokens = new Tokens();

        Assert.True(ChallengeEvents.ApplyMobPlaced(progress, tokens, now, "chicken"));
        Assert.False(ChallengeEvents.ApplyMobPlaced(progress, tokens, now, "chicken"));
        Assert.False(ChallengeEvents.ApplyMobPlaced(progress, tokens, now, "pig"));
        Assert.True(ChallengeEvents.ApplyMobPlaced(progress, tokens, now, "cow"));

        Assert.Equal(2, progress.GetSeasonChallengeProgress("1ccbe15e-3a72-4d8b-ad0b-00a23ac72eb1"));
    }

    [Fact]
    public void TheGoldPickaxeCheckpointAlsoCompletesSuperPrizes()
    {
        long now = At("2026-11-25T12:00:00Z");
        var rule = ChallengeEvents.GetEventRule("hostile_gold_pickaxe")!;
        var progress = ActiveOn(rule.ChallengeId, SuperPrizesId);
        var tokens = new Tokens();
        Assert.False(ChallengesController.GetSeasonChallengeStatus(progress, SuperPrizesId).Unlocked);

        for (int kill = 0; kill < 4; kill++)
        {
            ChallengeEvents.ApplyChallengeEvent(progress, tokens, now, "hostile_gold_pickaxe", rule);
        }

        Assert.Equal(1, progress.GetSeasonChallengeProgress(SuperPrizesId));
        Assert.True(ChallengesController.GetSeasonChallengeStatus(progress, SuperPrizesId).Complete);
        Assert.Contains(tokens.GetTokens(), token => token.Token is Tokens.ChallengeCompletedToken { ChallengeId: SuperPrizesId });
    }

    [Fact]
    public void TwoCompletedAdventuresCompleteThePlayAdventuresChallenge()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion { ActiveSeasonChallengeId = "e81d6ce8-622c-461a-a2d5-bb1f8ac36c62" };
        var tokens = new Tokens();

        ChallengeEvents.ApplyAdventureCompleted(progress, tokens, now);
        ChallengeEvents.ApplyAdventureCompleted(progress, tokens, now);

        Assert.Equal(2, progress.GetSeasonChallengeProgress("e81d6ce8-622c-461a-a2d5-bb1f8ac36c62"));
    }
}
