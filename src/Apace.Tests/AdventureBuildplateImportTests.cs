using Apace.ApiServer;

namespace Apace.Tests;

public sealed class AdventureBuildplateImportTests
{
    [Fact]
    public async Task FailingBuildplateDoesNotStopTheOthers()
    {
        using var data = new TempStaticData();
        foreach (string id in (string[])["aaaa", "bbbb", "cccc", "dddd"])
        {
            data.Write($"buildplates/adventures/{id}.zip", "");
        }

        List<string> attempts = [];
        var result = await Program.ImportAdventureBuildplatesAsync(
            data.Load().Buildplates.AdventureBuildplates,
            id => id == "dddd",
            buildplate =>
            {
                attempts.Add(buildplate.Id);
                if (buildplate.Id == "bbbb")
                {
                    return Task.FromException<bool>(new InvalidDataException("End of Central Directory record could not be found."));
                }

                return Task.FromResult(true);
            },
            3,
            TimeSpan.Zero);

        Assert.Equal((2, 1, 1), result);
        Assert.Equal(["aaaa", "bbbb", "cccc"], attempts.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RejectedBuildplateIsRetried()
    {
        using var data = new TempStaticData();
        data.Write("buildplates/adventures/aaaa.zip", "");

        int attempts = 0;
        var result = await Program.ImportAdventureBuildplatesAsync(
            data.Load().Buildplates.AdventureBuildplates,
            id => false,
            buildplate => Task.FromResult(++attempts == 3),
            12,
            TimeSpan.Zero);

        Assert.Equal((1, 0, 0), result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RejectedBuildplateIsNotRetriedOnceOneIsImported()
    {
        using var data = new TempStaticData();
        data.Write("buildplates/adventures/aaaa.zip", "");
        data.Write("buildplates/adventures/bbbb.zip", "");

        List<string> attempts = [];
        var result = await Program.ImportAdventureBuildplatesAsync(
            data.Load().Buildplates.AdventureBuildplates.OrderBy(buildplate => buildplate.Id, StringComparer.Ordinal),
            id => false,
            buildplate =>
            {
                attempts.Add(buildplate.Id);
                return Task.FromResult(buildplate.Id == "aaaa");
            },
            12,
            TimeSpan.Zero);

        Assert.Equal((1, 1, 0), result);
        Assert.Equal(["aaaa", "bbbb"], attempts);
    }

    [Fact]
    public async Task RetriesAreSharedByTheWholeImport()
    {
        using var data = new TempStaticData();
        foreach (string id in (string[])["aaaa", "bbbb", "cccc"])
        {
            data.Write($"buildplates/adventures/{id}.zip", "");
        }

        int attempts = 0;
        var result = await Program.ImportAdventureBuildplatesAsync(
            data.Load().Buildplates.AdventureBuildplates,
            id => false,
            buildplate =>
            {
                attempts++;
                return Task.FromResult(false);
            },
            2,
            TimeSpan.Zero);

        Assert.Equal((0, 3, 0), result);
        Assert.Equal(5, attempts);
    }
}
