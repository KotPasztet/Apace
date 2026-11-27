namespace Apace.Tests;

public sealed class AdventuresConfigTests
{
    [Fact]
    public void EntryWithoutNameIsStillInThePool()
    {
        using var data = new TempStaticData();
        data.Write("adventures/common/common-buildplates.json", """
            {
              "buildplates": [
                { "templateId": "aaaa", "weight": 10 }
              ]
            }
            """);

        var adventuresConfig = data.Load().AdventuresConfig;

        Assert.Equal("aaaa", adventuresConfig.PickTemplateForFolder("common", new Random(1)));
        Assert.Null(adventuresConfig.GetBuildplateName("aaaa"));
    }

    [Fact]
    public void EntryWithNameIsFoundByTemplateId()
    {
        using var data = new TempStaticData();
        data.Write("adventures/rare/rare-buildplates.json", """
            {
              "buildplates": [
                { "templateId": "bbbb.zip", "weight": 10, "name": "Home Demolition" },
                { "templateId": "cccc", "weight": 10, "name": " " }
              ]
            }
            """);

        var adventuresConfig = data.Load().AdventuresConfig;

        Assert.Equal("Home Demolition", adventuresConfig.GetBuildplateName("bbbb"));
        Assert.Null(adventuresConfig.GetBuildplateName("cccc"));
        Assert.Null(adventuresConfig.GetBuildplateName("dddd"));
    }

    [Fact]
    public void TemplateIdsListsEveryPoolEntryOnce()
    {
        using var data = new TempStaticData();
        data.Write("adventures/common/common-buildplates.json", """
            {
              "buildplates": [
                { "templateId": "aaaa", "weight": 10 },
                { "templateId": "bbbb.zip", "weight": 10 }
              ]
            }
            """);
        data.Write("adventures/rare/rare-buildplates.json", """
            {
              "buildplates": [
                { "templateId": "bbbb", "weight": 10 },
                { "templateId": "cccc", "weight": 10 }
              ]
            }
            """);

        var adventuresConfig = data.Load().AdventuresConfig;

        Assert.Equal(["aaaa", "bbbb", "cccc"], adventuresConfig.TemplateIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TemplateThatIsNotKeptIsNeverPicked()
    {
        using var data = new TempStaticData();
        data.Write("adventures/common/common-buildplates.json", """
            {
              "buildplates": [
                { "templateId": "aaaa", "weight": 10 },
                { "templateId": "bbbb", "weight": 10 }
              ]
            }
            """);

        var adventuresConfig = data.Load().AdventuresConfig;

        string[] removed = adventuresConfig.KeepTemplates(templateId => templateId == "aaaa");

        Assert.Equal(["bbbb"], removed);
        for (int seed = 0; seed < 20; seed++)
        {
            Assert.Equal("aaaa", adventuresConfig.PickTemplateForFolder("common", new Random(seed)));
        }
    }

    [Fact]
    public void PoolWithNoTemplateKeptGivesNoTemplateForItsCrystal()
    {
        using var data = new TempStaticData();
        data.Write("adventures/epic/epic-buildplates.json", """
            {
              "buildplates": [
                { "templateId": "bbbb", "weight": 10 }
              ]
            }
            """);

        var adventuresConfig = data.Load().AdventuresConfig;
        Assert.Equal("bbbb", adventuresConfig.TryPickTemplateForCrystalItem("adventure_crystal_epic", new Random(1)));

        adventuresConfig.KeepTemplates(templateId => false);

        Assert.Null(adventuresConfig.PickTemplateForFolder("epic", new Random(1)));
        Assert.Null(adventuresConfig.TryPickTemplateForCrystalItem("adventure_crystal_epic", new Random(1)));
    }

    [Fact]
    public void TemplateIdGivesTheFolderOfItsPool()
    {
        using var data = new TempStaticData();
        data.Write("adventures/common/common-buildplates.json", """
            {
              "buildplates": [
                { "templateId": "aaaa", "weight": 10 }
              ]
            }
            """);
        data.Write("adventures/rare/rare-buildplates.json", """
            {
              "buildplates": [
                { "templateId": "bbbb.zip", "weight": 10 }
              ]
            }
            """);

        var adventuresConfig = data.Load().AdventuresConfig;

        Assert.Equal("common", adventuresConfig.GetBuildplateFolder("aaaa"));
        Assert.Equal("rare", adventuresConfig.GetBuildplateFolder("bbbb"));
        Assert.Null(adventuresConfig.GetBuildplateFolder("cccc"));
    }
}
