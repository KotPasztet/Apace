using SData = Apace.StaticData.StaticData;

namespace Apace.Tests;

internal sealed class TempStaticData : IDisposable
{
    public TempStaticData()
    {
        Directory.CreateDirectory(Root);
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"apace-tests-{Guid.NewGuid():N}");

    public void Write(string relativePath, string content)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public SData Load()
        => new(Root);

    public void Dispose()
        => Directory.Delete(Root, true);
}
