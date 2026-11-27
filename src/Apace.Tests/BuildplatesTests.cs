namespace Apace.Tests;

public sealed class BuildplatesTests
{
    [Fact]
    public void AdventureBuildplatesAreEmptyWhenTheFolderIsMissing()
    {
        using var data = new TempStaticData();

        Assert.Empty(data.Load().Buildplates.AdventureBuildplates);
    }

    [Fact]
    public void AdventureBuildplatesAreTheZipFilesInTheFolder()
    {
        using var data = new TempStaticData();
        data.Write("buildplates/adventures/aaaa.zip", "");
        data.Write("buildplates/adventures/README.md", "");

        string[] ids = [.. data.Load().Buildplates.AdventureBuildplates.Select(buildplate => buildplate.Id)];

        Assert.Equal(["aaaa"], ids);
    }
}
