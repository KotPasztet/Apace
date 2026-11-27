using System.Text.Json;
using System.Text.RegularExpressions;
using Apace.ApiServer.Utils;
using Apace.Buildplate.Launcher;

namespace Apace.Tests;

public sealed partial class ChallengeEventsDatapackTests
{
    private static DirectoryInfo PackDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Apace.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return new DirectoryInfo(Path.Combine(dir.FullName, "staticdata-extras", "server_template_dir", "datapacks", "apace_challenge_events"));
    }

    private static IEnumerable<string> FunctionLines()
        => PackDir().EnumerateFiles("*.mcfunction", SearchOption.AllDirectories).SelectMany(file => File.ReadAllLines(file.FullName));

    [GeneratedRegex(@"say (\[apace-[a-z-]+\](?: [a-z_]+)?)$")]
    private static partial Regex SayMarker();

    [Fact]
    public void EveryLineThePackPrintsIsParsedAndCounted()
    {
        string[] said = [.. FunctionLines().Select(line => SayMarker().Match(line)).Where(match => match.Success).Select(match => match.Groups[1].Value).Distinct()];

        Assert.Contains("[apace-placed-chicken]", said);
        Assert.Contains("[apace-placed-cow]", said);
        Assert.Contains("[apace-event] hostile_tnt", said);

        foreach (string message in said)
        {
            var line = PersistentProcessManager.ParseChallengeLine($"[18:02:11] [Server thread/INFO]: [Not Secure] [6bd8769afeba7d52] {message}");
            Assert.NotNull(line);
            if (line.EventType == "challengeEvent")
            {
                Assert.NotNull(ChallengeEvents.GetEventRule(message["[apace-event] ".Length..]));
            }
        }
    }

    [Fact]
    public void ThePackTargetsMinecraft1204AndItsJsonParses()
    {
        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackDir().FullName, "pack.mcmeta")));
        Assert.Equal(26, meta.RootElement.GetProperty("pack").GetProperty("pack_format").GetInt32());

        foreach (FileInfo file in PackDir().EnumerateFiles("*.json", SearchOption.AllDirectories))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file.FullName));
        }
    }

    [Fact]
    public void TemplateDatapacksReplaceTheirOldCopyInTheWorld()
    {
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "apace-datapacks-" + Guid.NewGuid().ToString("N")));
        try
        {
            var template = new DirectoryInfo(Path.Combine(root.FullName, "template", "datapacks"));
            var world = new DirectoryInfo(Path.Combine(root.FullName, "world", "datapacks"));
            Directory.CreateDirectory(Path.Combine(template.FullName, "pack", "data", "ns", "functions"));
            File.WriteAllText(Path.Combine(template.FullName, "pack", "pack.mcmeta"), "new");
            File.WriteAllText(Path.Combine(template.FullName, "pack", "data", "ns", "functions", "tick.mcfunction"), "say hi");
            File.WriteAllText(Path.Combine(template.FullName, "zipped.zip"), "zip");
            Directory.CreateDirectory(Path.Combine(world.FullName, "pack"));
            File.WriteAllText(Path.Combine(world.FullName, "pack", "pack.mcmeta"), "old");
            File.WriteAllText(Path.Combine(world.FullName, "pack", "removed.txt"), "old");
            Directory.CreateDirectory(Path.Combine(world.FullName, "own"));

            PersistentProcessManager.SeedWorldDatapacks(template, world);

            Assert.Equal("new", File.ReadAllText(Path.Combine(world.FullName, "pack", "pack.mcmeta")));
            Assert.True(File.Exists(Path.Combine(world.FullName, "pack", "data", "ns", "functions", "tick.mcfunction")));
            Assert.False(File.Exists(Path.Combine(world.FullName, "pack", "removed.txt")));
            Assert.True(File.Exists(Path.Combine(world.FullName, "zipped.zip")));
            Assert.True(Directory.Exists(Path.Combine(world.FullName, "own")));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void NoTemplateDatapacksLeavesTheWorldAlone()
    {
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "apace-datapacks-" + Guid.NewGuid().ToString("N")));
        try
        {
            var world = new DirectoryInfo(Path.Combine(root.FullName, "world", "datapacks"));

            PersistentProcessManager.SeedWorldDatapacks(new DirectoryInfo(Path.Combine(root.FullName, "missing")), world);

            Assert.False(world.Exists);
        }
        finally
        {
            if (root.Exists)
            {
                root.Delete(true);
            }
        }
    }
}
