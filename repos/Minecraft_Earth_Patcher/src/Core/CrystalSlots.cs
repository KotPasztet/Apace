namespace MCEPatcher.Core;

public static class CrystalSlots
{
    public const string UiFileName = "genoa_adventure_crystals.json";

    private static readonly (string Original, string Patched)[] Replacements =
    [
        ("\"$genoa_item_size\": [ \"88px\", \"100%x\" ]", "\"$genoa_item_size\": [ \"70px\", \"100%x\" ]"),
        ("\"$item_renderer_size\": [ 105, 105 ]", "\"$item_renderer_size\": [ 84, 84 ]"),
        ("\"size\": [ 78, 78 ]", "\"size\": [ 62, 62 ]"),
        ("\"size\": [ 352, 88 ]", "\"size\": [ 350, 70 ]"),
        ("\"grid_dimensions\": [ 4, 1 ]", "\"grid_dimensions\": [ 5, 1 ]"),
    ];

    public static void Apply(string uiFilePath)
    {
        string text = File.ReadAllText(uiFilePath);
        File.WriteAllText(uiFilePath, ApplyToText(text));
    }

    public static string ApplyToText(string text)
    {
        if (Replacements.All(replacement => Count(text, replacement.Patched) == 1 && Count(text, replacement.Original) == 0))
        {
            return text;
        }

        foreach (var (original, _) in Replacements)
        {
            int count = Count(text, original);
            if (count != 1)
            {
                throw new InvalidDataException($"Expected '{original}' once in {UiFileName}, found it {count} times; the file may be different from the supported version");
            }
        }

        foreach (var (original, patched) in Replacements)
        {
            text = text.Replace(original, patched, StringComparison.Ordinal);
        }

        return text;
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
