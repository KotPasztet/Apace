using Apace.Buildplate.Connector.Model;
using Apace.Common;

namespace Apace.Tests;

public sealed class AdventureChestBrokenMessageTests
{
    [Fact]
    public void MessageFromTheModIsRead()
    {
        var message = AdventureChestBrokenMessage.Read("""{"minecraftId":"11b269ca-c5f6-36ac-9131-81895359b36d","minecraftName":"0000000000ABC001","blockState":"minecraft:pearlescent_froglight[axis=z]","x":3,"y":64,"z":-7}""");

        Assert.NotNull(message);
        Assert.Equal("11b269ca-c5f6-36ac-9131-81895359b36d", message.MinecraftId);
        Assert.Equal("0000000000ABC001", message.MinecraftName);
        Assert.Equal("minecraft:pearlescent_froglight[axis=z]", message.BlockState);
        Assert.Equal((3, 64, -7), (message.X, message.Y, message.Z));
    }

    [Fact]
    public void EscapedCharactersAreRead()
    {
        var message = AdventureChestBrokenMessage.Read("""{"minecraftId":"11b269ca-c5f6-36ac-9131-81895359b36d","minecraftName":"a\"b\\c\u000ad\u0001e\u00e9f\u007fg\ud83d\ude00h","blockState":"minecraft:ochre_froglight[axis=x]","x":-2,"y":44,"z":1}""");

        Assert.NotNull(message);
        Assert.Equal("a\"b\\c\nd\u0001e\u00e9f\u007fg\ud83d\ude00h", message.MinecraftName);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var message = AdventureChestBrokenMessage.Read("""{"minecraftId":"a","minecraftName":"b","blockState":"c","x":1,"y":2,"z":3,"dimension":"fountain:instance_x"}""");

        Assert.NotNull(message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"adventureChestBroken\"")]
    [InlineData("""{"minecraftId":"a","minecraftName":"b","blockState":"c","x":1,"y":2}""")]
    [InlineData("""{"minecraftId":"a","minecraftName":"b","blockState":"c","x":"1","y":2,"z":3}""")]
    [InlineData("""{"minecraftId":"a","minecraftName":"b","blockState":"c","x":1.5,"y":2,"z":3}""")]
    [InlineData("""{"minecraftId":"a","minecraftName":"b","blockState":"c","x":2147483648,"y":2,"z":3}""")]
    [InlineData("""{"minecraftId":"a","minecraftName":"b","blockState":null,"x":1,"y":2,"z":3}""")]
    [InlineData("""{"minecraftId":"a","minecraftName":"","blockState":"c","x":1,"y":2,"z":3}""")]
    [InlineData("""{"minecraftName":"b","blockState":"c","x":1,"y":2,"z":3}""")]
    [InlineData("""{"minecraftId":1,"minecraftName":"b","blockState":"c","x":1,"y":2,"z":3}""")]
    public void BadMessageIsNull(string json)
        => Assert.Null(AdventureChestBrokenMessage.Read(json));

    [Fact]
    public void ForwardedMessageIsPrintableAscii()
    {
        // the event bus only accepts printable ASCII, the launcher sends this on to the API server
        var message = new AdventureChestBrokenMessage("11b269ca-c5f6-36ac-9131-81895359b36d", "a\"b\\c\nd\u0001e\u00e9f\u007fg\ud83d\ude00h<&>", "minecraft:ochre_froglight[axis=x]", -2, 44, 1);

        string json = Json.Serialize(message);

        Assert.All(json, character => Assert.InRange(character, (char)32, (char)126));
        Assert.DoesNotContain("isComplete", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(message, AdventureChestBrokenMessage.Read(json));
    }
}
