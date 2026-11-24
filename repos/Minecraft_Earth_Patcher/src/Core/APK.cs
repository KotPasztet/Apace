using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Serilog;

namespace MCEPatcher.Core;

public static class APK
{
    public const string FileName = "apktool.jar";
    public const string FileNameBat = "apktool.bat";

    // aapt2 binary the build falls back to when the one bundled in apktool.jar
    // cannot run on this machine (see ProbeBundledAapt2). Shipped by the distro
    // "aapt" package (Debian/Ubuntu put it at /usr/bin/aapt2); its arm64 build
    // targets the generic ARMv8-A baseline, so it runs on old CPUs where the
    // bundled binary SIGILLs.
    internal const string SystemAapt2Path = "/usr/bin/aapt2";

    public static bool Decode(FileInfo apk, DirectoryInfo output)
    {
        List<string> args =
        [
            "d",
            "-f",
        ];

        // Optional framework directory (see SystemTools). apktool 3.x bundles
        // a framework whose resources table some aapt2 builds (e.g. Debian's)
        // reject, so a well-formed one (android.jar) can be supplied instead.
        if (SystemTools.ApktoolFramePath is { } framePath)
        {
            args.Add("--frame-path");
            args.Add($"{framePath}");
        }

        args.Add("-o");
        args.Add($"{output.FullName}");
        args.Add($"{apk.FullName}");

        Process process;
        if (OperatingSystem.IsWindows() && File.Exists(FileNameBat))
        {
            process = U.Run(Path.GetFullPath(FileNameBat), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), [.. args]);
        }
        else
        {
            process = U.Run("java", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            [
                "-jar", $"{Path.GetFullPath(FileName)}",
                .. args,
            ]);
        }

        process.StandardInput.Write(" "); // Press any key to continue . . .

        process.WaitForExit();
        int exitCode = process.ExitCode;
        process.Close();
        process.Dispose();

        return exitCode is 0;
    }

    public static bool Encode(DirectoryInfo input, FileInfo outApk)
    {
        outApk.Delete();

        // Pre-flight for the aapt2 binary apktool bundles inside its own jar
        // and executes during the resource step ("I: Building resources with
        // aapt2..."). On some ARM hosts that binary dies with SIGILL (exit
        // code 132): the bundled build targets a newer CPU baseline than the
        // host has (e.g. the user's phone, while a Raspberry Pi runs the same
        // binary fine). Executing it once with `version` BEFORE the build
        // detects this; a failed probe switches THIS build to the system
        // aapt2 (see SystemAapt2Path) so those hosts still build. Healthy
        // hosts pass the probe and keep the bundled binary unchanged - zero
        // regression there. Skipped entirely when AAPT2_PATH overrides the
        // binary (see SystemTools), because apktool then never touches the
        // bundled one.
        bool useSystemAapt;

        if (SystemTools.Aapt2Path is not null)
        {
            useSystemAapt = false;
        }
        else
        {
            // a missing system binary must not turn a currently-failing
            // build into a differently-failing one - stay on the bundled
            // binary in that case
            useSystemAapt = !ProbeBundledAapt2(Path.GetFullPath(FileName)) && File.Exists(SystemAapt2Path);

            Log.Information(useSystemAapt ? "using system aapt (fallback)" : "using bundled aapt2");
        }

        List<string> args = BuildArgs(input, outApk.FullName, useSystemAapt);

        Process process;
        if (OperatingSystem.IsWindows() && File.Exists(FileNameBat))
        {
            process = U.Run(Path.GetFullPath(FileNameBat), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), [.. args]);
        }
        else
        {
            process = U.Run("java", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            [
                "-jar", $"{Path.GetFullPath(FileName)}",
                .. args,
            ]);
        }

        process.StandardInput.Write(" "); // Press any key to continue . . .

        process.WaitForExit();
        int exitCode = process.ExitCode;
        process.Close();
        process.Dispose();

        if (exitCode is not 0)
        {
            return false;
        }

        outApk.Refresh();
        return outApk.Exists;
    }

    // Builds the flag list for `apktool b`. Internal static so tests can assert
    // the exact invocation of both aapt branches (bundled vs fallback) via
    // reflection against the built DLL, without running apktool.
    internal static List<string> BuildArgs(DirectoryInfo input, string outApkPath, bool useSystemAapt)
    {
        List<string> args =
        [
            "b",
            "-f",
            "-o", $"{outApkPath}",
        ];

        // apktool 3.x bundles an x86-64 aapt2 only; on other hosts (e.g. ARM64
        // servers) the bundled binary cannot be executed, so allow overriding
        // it with a native one from the system (AAPT2_PATH, see SystemTools).
        if (SystemTools.Aapt2Path is { } aapt2)
        {
            args.Add("--aapt");
            args.Add($"{aapt2}");
        }
        else if (useSystemAapt)
        {
            // Pre-flight fallback (see Encode): the bundled aapt2 failed its
            // probe on this CPU. apktool 3.0.3 builds resources with aapt2
            // ONLY - aapt1 support was removed (no --use-aapt1 flag), so
            // --aapt must point at an aapt2 binary: the distro build at
            // /usr/bin/aapt2, not the aapt1 /usr/bin/aapt.
            args.Add("--aapt");
            args.Add(SystemAapt2Path);
        }

        // Optional framework directory (see SystemTools). apktool 3.x bundles
        // a framework whose resources table some aapt2 builds (e.g. Debian's)
        // reject, so a well-formed one (android.jar) can be supplied instead.
        if (SystemTools.ApktoolFramePath is { } framePath)
        {
            args.Add("--frame-path");
            args.Add($"{framePath}");
        }

        args.Add($"{input.FullName}");

        return args;
    }

    // The aapt2 binary apktool 3.0.3 bundles one per OS (no per-arch entries;
    // see AaptManager in the apktool sources) and extracts from its own jar to
    // a temp file before running it during the resource build.
    private static string? BundledAapt2Entry()
    {
        if (OperatingSystem.IsLinux())
        {
            return "prebuilt/linux/aapt2";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "prebuilt/macosx/aapt2";
        }

        if (OperatingSystem.IsWindows())
        {
            return "prebuilt/windows/aapt2.exe";
        }

        return null;
    }

    /// <summary>
    /// Pre-flight for the aapt2 binary bundled in apktool.jar: extracts it from
    /// the jar and executes it with the <c>version</c> subcommand - the same
    /// probe apktool itself performs (AaptManager.getBinaryVersion); note aapt2
    /// has no --version option. Reports whether the binary can run here.
    /// </summary>
    /// <remarks>
    /// The bundled linux aapt2 in apktool 3.0.3 targets a CPU baseline newer
    /// than some ARM hosts have: there it dies with SIGILL (exit code 132)
    /// inside "I: Building resources with aapt2...", failing the whole build
    /// (e.g. on the user's phone, while a Raspberry Pi runs the same binary
    /// fine). Any non-zero exit (132, 126, ...) - or a failure to start at all
    /// (a foreign-arch ELF execs to ENOEXEC, which throws instead of exiting) -
    /// reports <c>false</c>, and Encode switches this build to
    /// <see cref="SystemAapt2Path"/>. On healthy hosts the probe succeeds and
    /// nothing changes. Extraction failures report <c>true</c>: a broken jar
    /// must not alter the current behavior (apktool handles its own extraction
    /// failures).
    /// </remarks>
    internal static bool ProbeBundledAapt2(string jarPath)
    {
        try
        {
            string? entryName = BundledAapt2Entry();

            if (entryName is null || !File.Exists(jarPath))
            {
                return true; // nothing to probe - keep the current (bundled) behavior
            }

            string binaryPath = Path.Combine(Path.GetTempPath(), $"aapt2-preflight-{Guid.NewGuid():N}");

            try
            {
                using ZipArchive jar = ZipFile.OpenRead(jarPath);
                ZipArchiveEntry? entry = jar.GetEntry(entryName);

                if (entry is null)
                {
                    return true; // not bundled for this platform - keep the current behavior
                }

                entry.ExtractToFile(binaryPath, overwrite: true);

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        binaryPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    );
                }
            }
            catch
            {
                // a broken/missing jar must not change the current behavior
                return true;
            }

            try
            {
                var process = U.Run(binaryPath, Path.GetTempPath(), "version");
                process.WaitForExit();
                int exitCode = process.ExitCode;
                process.Close();
                process.Dispose();

                return exitCode is 0;
            }
            catch
            {
                // the binary did not even start (e.g. ENOEXEC for a foreign-arch
                // ELF) - apktool would fail the same way, so fall back
                return false;
            }
            finally
            {
                try
                {
                    File.Delete(binaryPath);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
        catch
        {
            // the probe is strictly best-effort: any unexpected error keeps
            // the current (bundled) behavior
            return true;
        }
    }
}