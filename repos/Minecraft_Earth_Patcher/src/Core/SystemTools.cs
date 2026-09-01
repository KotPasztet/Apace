namespace MCEPatcher.Core;

/// <summary>
/// Optional overrides for the external Android build tools.
///
/// apktool 3.x and Google's build-tools ship prebuilt binaries for x86-64
/// Linux only; on other architectures (e.g. ARM64 servers) the JVM cannot
/// execute the bundled aapt2 / zipalign. Setting <c>AAPT2_PATH</c> /
/// <c>ZIPALIGN_PATH</c> (e.g. to Debian's <c>/usr/bin/aapt2</c> and
/// <c>/usr/bin/zipalign</c>) makes the patcher use native tools from the
/// system instead.
/// </summary>
public static class SystemTools
{
    /// <summary>aapt2 binary used by apktool during the build step (passed via --aapt).</summary>
    public static string? Aapt2Path => Resolve("AAPT2_PATH");

    /// <summary>zipalign binary used instead of the one from Google's build-tools.</summary>
    public static string? ZipAlignPath => Resolve("ZIPALIGN_PATH");

    private static string? Resolve(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return File.Exists(value) ? value : null;
    }
}