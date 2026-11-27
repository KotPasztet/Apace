using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Apace.ApiServer.Controllers.EarthApi;
using Apace.ApiServer.Utils;
using Apace.Common;
using Apace.DB.Models.Player;
using ApiToken = Apace.ApiServer.Types.Common.Token;
using DBRewards = Apace.DB.Models.Common.Rewards;

namespace Apace.Tests;

public sealed class AdventureRewardTokenTests
{
    [Theory]
    [InlineData("adv_zyki", 0x04f45b46u)]
    [InlineData("challenge.progress", 0x29484713u)]
    [InlineData("challenge.completed", 0x132ecad9u)]
    public void KnownClientTypesHashToTheClientTable(string clientType, uint expected)
        => Assert.Equal(expected, ClientTokenTypes.Fnv1a32(clientType));

    [Fact]
    public void AdventureRewardClientTypeHashesToTheClientTable()
    {
        string attributeValue = typeof(ApiToken.Type)
            .GetField(nameof(ApiToken.Type.ADVENTURE_REWARD))!
            .GetCustomAttribute<JsonStringEnumMemberNameAttribute>()!
            .Name;

        Assert.Equal("adventure.reward.khx6bcq", attributeValue);
        Assert.Equal(0xe37ab0e7u, ClientTokenTypes.Fnv1a32(attributeValue));
        Assert.Equal(ClientTokenTypes.AdventureRewardHash, ClientTokenTypes.Fnv1a32(attributeValue));
    }

    [Fact]
    public void AdventureRewardClientTypeIsWhatTheClientIsSent()
    {
        string serialized = Json.Serialize(ApiToken.Type.ADVENTURE_REWARD);

        Assert.Equal("\"adventure.reward.khx6bcq\"", serialized);
        Assert.Equal(0xe37ab0e7u, ClientTokenTypes.Fnv1a32(serialized.Trim('"')));
    }

    // an adventure reward token as it was served to a client that went on to redeem it
    [Fact]
    public void TokenIsSentInTheShapeTheClientRedeems()
    {
        const string expected = """
            {
              "clientType": "adventure.reward.khx6bcq",
              "clientProperties": { "instanceId": "5a19a36d-fd51-4c59-8390-7b7d24c5ebe7" },
              "rewards": {
                "rubies": 0,
                "experiencePoints": 0,
                "level": null,
                "inventory": [
                  { "id": "81a84b7e-928f-7157-254c-6543e90dbc59", "amount": 1 },
                  { "id": "a3435b17-e552-4390-8853-908b24dbba6c", "amount": 1 },
                  { "id": "9a0b3501-a1db-0d8c-b04a-f82a9d9d8c72", "amount": 1 },
                  { "id": "0d78acb3-3dc9-bbc4-01ae-8b10c76867f9", "amount": 1 }
                ],
                "buildplates": [],
                "challenges": [],
                "personaItems": [],
                "utilityBlocks": []
              },
              "lifetime": "Transient"
            }
            """;

        var token = new Tokens.AdventureRewardToken(
            "5a19a36d-fd51-4c59-8390-7b7d24c5ebe7",
            new DBRewards(0, 0, null, new Dictionary<string, int?>
            {
                ["81a84b7e-928f-7157-254c-6543e90dbc59"] = 1,
                ["a3435b17-e552-4390-8853-908b24dbba6c"] = 1,
                ["9a0b3501-a1db-0d8c-b04a-f82a9d9d8c72"] = 1,
                ["0d78acb3-3dc9-bbc4-01ae-8b10c76867f9"] = 1,
            }, [], []));

        string actual = Json.Serialize(TokensController.TokenToApiResponse(token));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), actual);
    }

    [Fact]
    public void RedeemAnswersWithTheTokenAndAnUpdatesKey()
    {
        var token = new Tokens.ChallengeCompletedToken("e81d6ce8-622c-461a-a2d5-bb1f8ac36c62", "10986d40-516f-459b-bc24-a16296998c1e");

        JsonObject result = JsonNode.Parse(Json.Serialize(TokensController.RedeemResponse(token)))!.AsObject();

        Assert.True(result.ContainsKey("updates"));
        Assert.Null(result["updates"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Json.Serialize(TokensController.TokenToApiResponse(token))), result["token"]));
    }

    [Fact]
    public void TokenSurvivesTheDatabase()
    {
        var tokens = new Tokens();
        tokens.AddToken("a34bcfbc-aa61-48ae-8f62-3128ec0b1127", new Tokens.AdventureRewardToken(
            "5a19a36d-fd51-4c59-8390-7b7d24c5ebe7",
            new DBRewards(0, 0, null, new Dictionary<string, int?> { ["fd595ab4-a2c8-b278-caf7-91021fd32811"] = 2 }, [], [])));

        Tokens? read = Json.Deserialize<Tokens>(Json.Serialize(tokens));

        var token = Assert.IsType<Tokens.AdventureRewardToken>(Assert.Single(read!.GetTokens()).Token);
        Assert.Equal(Tokens.Token.TypeE.ADVENTURE_REWARD, token.Type);
        Assert.Equal("5a19a36d-fd51-4c59-8390-7b7d24c5ebe7", token.InstanceId);
        Assert.Equal(2, token.Rewards.Items["fd595ab4-a2c8-b278-caf7-91021fd32811"]);
    }
}
