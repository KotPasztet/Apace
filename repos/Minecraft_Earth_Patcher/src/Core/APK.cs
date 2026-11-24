using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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
    // bundled binary SIGILLs. The raw binary is older than what apktool 3.0.3
    // expects, so it is only ever used through the flag-filtering wrapper
    // written next to apktool.jar (see WriteSystemAapt2Wrapper).
    internal const string SystemAapt2Path = "/usr/bin/aapt2";

    // flag-filtering wrapper the Apace Dockerfile installs on top of the
    // distro binary (REAL_AAPT2=/usr/bin/aapt2; AAPT2_PATH inside the image
    // points at it). Searched BEFORE the raw distro binary (see
    // SystemAapt2CandidatePaths) so Docker-derived rootfs' pick the same
    // wrapper the image itself uses.
    internal const string DockerWrapperAapt2Path = "/usr/local/bin/aapt2";

    // Where ResolveSystemAapt2 looks for a system aapt2, in order (after the
    // APACE_SYS_AAPT2 / AAPT2_PATH overrides). Internal (not readonly) so the
    // stub harness can point the search at sandbox paths; production code
    // never mutates it.
    internal static string[] SystemAapt2CandidatePaths = [DockerWrapperAapt2Path, SystemAapt2Path];

    // apt-get used by the self-heal path (see SelfHealInstallAapt). Internal
    // (not const) for the same stub-harness reason.
    internal static string AptGetPath = "/usr/bin/apt-get";

    // name of the generated system-aapt2 wrapper inside the patcher working
    // directory (the same directory apktool.jar lives in)
    internal const string SystemAapt2WrapperFileName = "aapt2-system.sh";

    // how long a smoke-compile candidate may run before it gets killed (see
    // ProbeAapt2Compile)
    internal const int ProbeTimeoutMilliseconds = 45_000;

    // how long `<candidate> version` may run during the candidate search
    // before it gets killed (see IsWorkingAapt2Candidate)
    internal const int VersionProbeTimeoutMilliseconds = 15_000;

    // how long each apt-get command of the self-heal path may run before it
    // gets killed (see SelfHealInstallAapt): long enough for a slow phone's
    // apt-get over mobile data, short enough that a wedged network cannot
    // hang a patch job
    internal const int SelfHealTimeoutMilliseconds = 180_000;

    // the apt self-heal is attempted at most once per process lifetime
    // (0 = not yet attempted, 1 = attempted): the patcher runs inside the
    // long-lived Apace server, and every job on a host that still needs it
    // would otherwise re-run apt-get (see SelfHealInstallAapt)
    private static int selfHealAttempted;

    // synthetic probe exit codes for candidates that never produced a real
    // one (used in the fallback diagnostics): could not be started at all
    // (e.g. ENOEXEC for a foreign-arch ELF), killed after the probe timeout,
    // or exited 0 without producing the output zip
    internal const int ProbeFailedToStart = -1;
    internal const int ProbeTimedOut = -2;
    internal const int ProbeNoOutput = -3;

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
        // binary fine). Running a real mini-compile with it BEFORE the build
        // (the same `aapt2 compile` invocation apktool 3.0.3 itself uses, see
        // ProbeAapt2Compile - a mere `version` call exits 0 even on hosts
        // where the real compile then SIGILLs) detects this; a failed probe
        // switches THIS build to the system aapt2 through a wrapper that
        // drops the modern flags the distro binary rejects (see
        // WriteSystemAapt2Wrapper) so those hosts still build. When the host
        // has no system aapt2 at all (e.g. a stale phone rootfs that predates
        // the Docker image's 'aapt' package) the search falls through to an
        // apt self-heal that installs the package once and re-scans (see
        // SelfHealInstallAapt). Healthy hosts pass the probe and keep the
        // bundled binary unchanged - zero regression there, and the self-heal
        // never runs. Skipped entirely when AAPT2_PATH overrides the
        // binary (see SystemTools), because apktool then never touches the
        // bundled one.
        string? systemAapt2 = null; // --aapt override for this build, or null for the bundled binary

        if (SystemTools.Aapt2Path is null)
        {
            if (SelectAapt2(Path.GetFullPath(FileName), out systemAapt2) is Aapt2Selection.Unusable)
            {
                return false; // SelectAapt2 already logged both probe diagnostics
            }
        }

        List<string> args = BuildArgs(input, outApk.FullName, systemAapt2);

        var buildOutput = new StringBuilder();
        int exitCode = RunApktool(args, buildOutput);

        // Retry net: the smoke probe is a heuristic - under emulation the
        // bundled binary's SIGILL is FLAKY and can spare the tiny probe but
        // still kill the real build (user log 2026-11-24 20:02: probe passed,
        // build died with "exit code = 132"). When a bundled-aapt2 build
        // dies the way the probe is meant to catch (exec failure / killed by
        // a signal, see IsAapt2ExecFailure; resource errors never match and
        // surface unchanged), give the build ONE more shot through the
        // system wrapper - running the candidate search (and, if it comes up
        // empty, the apt self-heal, see SelfHealInstallAapt) at THIS point,
        // because the pre-flight may have passed only by fluke. Never
        // retried when the first attempt already used the wrapper or
        // AAPT2_PATH, and never more than once.
        if (exitCode is not 0 && systemAapt2 is null && SystemTools.Aapt2Path is null && IsAapt2ExecFailure(buildOutput.ToString(), out int bundledExitCode))
        {
            string? retryAapt2 = PrepareSystemAapt2Fallback();

            if (retryAapt2 is not null)
            {
                Log.Warning($"bundled aapt2 failed (exit={bundledExitCode}) - retrying the build once with system aapt2 ({retryAapt2})");

                buildOutput.Clear();
                exitCode = RunApktool(BuildArgs(input, outApk.FullName, retryAapt2), buildOutput);
            }
            else
            {
                string exitHint = bundledExitCode switch
                {
                    132 => " = 128 + SIGILL",
                    ProbeFailedToStart => " (process never started)",
                    _ => "",
                };

                Log.Error(
                    $"aapt2: the build failed with the aapt2 bundled in apktool.jar (exit={bundledExitCode}{exitHint}) " +
                    "and no working system aapt2 could be found or installed - the bundled binary cannot run on this device's CPU; " +
                    "installing the 'aapt' package (apt-get install -y aapt) or pointing AAPT2_PATH at a working aapt2 binary fixes it"
                );
            }
        }

        if (exitCode is not 0)
        {
            return false;
        }

        outApk.Refresh();
        return outApk.Exists;
    }

    // Runs `apktool <args>` and returns its exit code, optionally capturing
    // the combined [OUT]/[ERR] lines (the Serilog logging inside U.Run stays
    // exactly the same). Used by Encode for the build attempt and its retry.
    private static int RunApktool(List<string> args, StringBuilder? output)
    {
        Process process;
        if (OperatingSystem.IsWindows() && File.Exists(FileNameBat))
        {
            process = U.Run(Path.GetFullPath(FileNameBat), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), output, [.. args]);
        }
        else
        {
            process = U.Run("java", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), output,
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

        return exitCode;
    }

    // Builds the flag list for `apktool b`. Internal static so tests can assert
    // the exact invocation of both aapt branches (bundled vs fallback) via
    // reflection against the built DLL, without running apktool.
    internal static List<string> BuildArgs(DirectoryInfo input, string outApkPath, string? systemAapt2)
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
        else if (systemAapt2 is { } wrapperPath)
        {
            // Pre-flight fallback (see Encode): the bundled aapt2 failed its
            // smoke compile on this CPU. apktool 3.0.3 builds resources with
            // aapt2 ONLY - aapt1 support was removed (no --use-aapt1 flag) -
            // and passes flags the distro aapt2 rejects (see
            // WriteSystemAapt2Wrapper), so --aapt points at the generated
            // wrapper, never at the raw /usr/bin/aapt2.
            args.Add("--aapt");
            args.Add($"{wrapperPath}");
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
    /// Pre-flight for the aapt2 binary bundled in apktool.jar: extracts it
    /// from the jar - the same entry apktool's own AaptManager picks (see
    /// <see cref="BundledAapt2Entry"/>) - to a temp file and smoke-compiles
    /// it with <see cref="ProbeAapt2Compile"/>. Reports whether the binary
    /// can actually compile resources on this machine.
    /// </summary>
    /// <remarks>
    /// The bundled linux aapt2 in apktool 3.0.3 targets a CPU baseline newer
    /// than some ARM hosts have: there it dies with SIGILL (exit code 132)
    /// inside "I: Building resources with aapt2...", failing the whole build
    /// (e.g. on the user's phone, while a Raspberry Pi runs the same binary
    /// fine). Any non-zero exit (132, 126, ...) - or a failure to start at all
    /// (a foreign-arch ELF execs to ENOEXEC, which throws instead of exiting)
    /// - reports failure, and Encode switches this build to the system aapt2
    /// wrapper (see <see cref="WriteSystemAapt2Wrapper"/>). On healthy hosts
    /// the probe succeeds and nothing changes. Extraction failures report
    /// success (<c>Ok = true</c>): a broken jar must not alter the current
    /// behavior (apktool handles its own extraction failures, and the retry
    /// net in Encode catches a later exec failure anyway).
    /// </remarks>
    internal static (bool Ok, int ExitCode) ProbeBundledAapt2(string jarPath)
    {
        try
        {
            string? entryName = BundledAapt2Entry();

            if (entryName is null || !File.Exists(jarPath))
            {
                return (true, 0); // nothing to probe - keep the current (bundled) behavior
            }

            string binaryPath = Path.Combine(Path.GetTempPath(), $"aapt2-preflight-{Guid.NewGuid():N}");

            try
            {
                using ZipArchive jar = ZipFile.OpenRead(jarPath);
                ZipArchiveEntry? entry = jar.GetEntry(entryName);

                if (entry is null)
                {
                    return (true, 0); // not bundled for this platform - keep the current behavior
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
                return (true, 0);
            }

            try
            {
                return ProbeAapt2Compile(binaryPath);
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
            return (true, 0);
        }
    }

    // Which aapt2 the upcoming apktool build uses (see SelectAapt2).
    internal enum Aapt2Selection
    {
        /// <summary>apktool's own bundled binary (no --aapt flag passed).</summary>
        Bundled,

        /// <summary>The generated system-aapt2 wrapper (see WriteSystemAapt2Wrapper), passed via --aapt.</summary>
        SystemWrapper,

        /// <summary>Bundled and system candidates both failed their smoke probe - the job must fail early.</summary>
        Unusable,
    }

    // Pre-flight aapt2 selection for one build (see Encode): smoke-compiles
    // the bundled binary and, when that fails, resolves a system aapt2
    // (running the apt self-heal first when the host has none at all, see
    // SelfHealInstallAapt), smoke-compiles the generated wrapper, and logs
    // the decision. Internal static so tests can drive the selection
    // (including its logging) via reflection against the built DLL, without
    // running apktool.
    internal static Aapt2Selection SelectAapt2(string jarPath, out string? systemAapt2)
    {
        systemAapt2 = null;

        var (bundledOk, bundledExitCode) = ProbeBundledAapt2(jarPath);

        if (bundledOk)
        {
            Log.Information("using bundled aapt2");
            return Aapt2Selection.Bundled;
        }

        // the bundled binary cannot compile here - can the system one? (and
        // if the host has none at all: can we install one? The self-heal
        // below only ever runs in THIS branch, i.e. strictly after the
        // bundled probe failed - see its STRICT GATE remark)
        string? systemBinary = ResolveSystemAapt2() ?? SelfHealInstallAapt();

        if (systemBinary is null)
        {
            // a missing system binary must not turn a currently-failing build
            // into a differently-failing one - stay on the bundled binary (the
            // retry net in Encode catches a later exec failure)
            Log.Warning($"aapt2 pre-flight: bundled aapt2 failed (exit={bundledExitCode}) and no system aapt2 was found - building with the bundled aapt2 anyway");
            Log.Information("using bundled aapt2");
            return Aapt2Selection.Bundled;
        }

        string wrapperPath = WriteSystemAapt2Wrapper(systemBinary);
        var (wrapperOk, wrapperExitCode) = ProbeAapt2Compile(wrapperPath);

        if (wrapperOk)
        {
            Log.Warning($"aapt2 pre-flight: bundled aapt2 failed (exit={bundledExitCode}) - falling back to system aapt2 ({systemBinary})");
            Log.Information($"using system aapt2 ({wrapperPath})");
            systemAapt2 = wrapperPath;
            return Aapt2Selection.SystemWrapper;
        }

        Log.Error(
            $"aapt2 pre-flight: bundled aapt2 failed (exit={bundledExitCode}, apktool.jar at '{jarPath}') and the system aapt2 wrapper failed too " +
            $"(exit={wrapperExitCode}, wrapper '{wrapperPath}', system binary '{systemBinary}') - cannot build resources on this host; " +
            "point AAPT2_PATH at a working aapt2 if one exists"
        );
        return Aapt2Selection.Unusable;
    }

    // Resolves the system aapt2 the fallback goes through. Candidates, in
    // order: APACE_SYS_AAPT2 (test/host override, honored only when the file
    // exists and answers `version` - lets hosts and tests point somewhere
    // else), AAPT2_PATH (the same variable SystemTools uses; when it names a
    // working binary Encode passes it via --aapt directly and never gets
    // here, so this only ever catches a stale value), the well-known paths in
    // SystemAapt2CandidatePaths (the flag-filtering wrapper from the Apace
    // Dockerfile first, then the raw binary of the Debian/Ubuntu "aapt"
    // package), and finally a PATH lookup through /bin/sh (`command -v aapt2`)
    // for everything non-standard. Each candidate must answer `version` with
    // exit 0 (see IsWorkingAapt2Candidate) before it is accepted; the
    // accepted binary is still only ever used through the generated wrapper
    // and its real-compile probe (see SelectAapt2 / WriteSystemAapt2Wrapper).
    // Null when nothing usable exists. On Windows every candidate path is
    // absent, so the wrapper-based fallback is effectively disabled there -
    // same behavior as before it existed.
    internal static string? ResolveSystemAapt2()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        // APACE_SYS_AAPT2: test/host override for the system aapt2 location
        string? overridden = Environment.GetEnvironmentVariable("APACE_SYS_AAPT2");

        if (IsWorkingAapt2Candidate(overridden))
        {
            return overridden;
        }

        string? fromAapt2Path = Environment.GetEnvironmentVariable("AAPT2_PATH");

        if (IsWorkingAapt2Candidate(fromAapt2Path))
        {
            return fromAapt2Path;
        }

        foreach (string candidate in SystemAapt2CandidatePaths)
        {
            if (IsWorkingAapt2Candidate(candidate))
            {
                return candidate;
            }
        }

        string? onPath = FindAapt2OnPath();

        return IsWorkingAapt2Candidate(onPath) ? onPath : null;
    }

    // Quick candidacy check for one system-aapt2 location (see
    // ResolveSystemAapt2): the file must exist and `version` must exit 0 -
    // the cheapest command that proves the file is an executable aapt2 for
    // THIS machine (a foreign-arch ELF, a stale or broken wrapper fails to
    // start or exits non-zero). This is deliberately NOT the real-compile
    // probe (see ProbeAapt2Compile): it only picks WHICH binary the fallback
    // wrapper execs; the wrapper itself must still pass the real compile
    // probe before any build uses it. Internal static for the stub harness.
    internal static bool IsWorkingAapt2Candidate(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
        {
            return false;
        }

        try
        {
            var process = U.Run(candidatePath, Path.GetTempPath(), "version");
            int? exitCode = WaitForExitOrKill(process, VersionProbeTimeoutMilliseconds);

            return exitCode is 0;
        }
        catch
        {
            return false; // did not even start (not executable, ENOEXEC, ...)
        }
    }

    // Last resort of the candidate search (see ResolveSystemAapt2): ask the
    // system shell where aapt2 is on PATH, through /bin/sh - present in every
    // Debian/Ubuntu rootfs (incl. the proot rootfs on the phone) and never
    // required on Windows (ResolveSystemAapt2 has already returned null
    // there). `command -v` is POSIX; `which` is not. Returns the path or null.
    private static string? FindAapt2OnPath()
    {
        if (!File.Exists("/bin/sh"))
        {
            return null;
        }

        try
        {
            var output = new StringBuilder();
            var process = U.Run("/bin/sh", Path.GetTempPath(), output, "-c", "command -v aapt2");
            int? exitCode = WaitForExitOrKill(process, VersionProbeTimeoutMilliseconds);

            if (exitCode is not 0)
            {
                return null; // not on PATH, or the lookup itself failed / timed out
            }

            string candidate = output.ToString().Trim();

            return candidate.Length > 0 && File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    // Writes (idempotently) the wrapper the system-aapt2 fallback runs
    // through, into the same directory apktool.jar lives in, and returns its
    // path. Mirrors the /usr/local/bin/aapt2 wrapper from the Apace Dockerfile
    // exactly: apktool 3.0.3 passes a few modern aapt2 flags unconditionally
    // (--no-compile-sdk-metadata, --enable-compact-entries, --feature-flags)
    // that the older aapt2 shipped by Debian/Ubuntu (package "aapt") aborts
    // the link step on with "unknown option"; the wrapper drops exactly those
    // flags and execs the real binary. That is safe because they only DISABLE
    // optional behaviour the old aapt2 never had in the first place. Passing
    // the raw distro binary via --aapt would fail the link step.
    internal static string WriteSystemAapt2Wrapper(string systemAapt2)
    {
        string wrapperPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(FileName)) ?? ".", SystemAapt2WrapperFileName);

        string script =
            $$"""
            #!/bin/bash
            # aapt2 wrapper: filters flags unsupported by this aapt2 build.
            # Generated at runtime by MCEPatcher (see APK.WriteSystemAapt2Wrapper);
            # mirrors the /usr/local/bin/aapt2 wrapper from the Apace Dockerfile.
            set -eu
            REAL_AAPT2={{systemAapt2}}
            args=()
            while [ $# -gt 0 ]; do
                case "$1" in
                    --no-compile-sdk-metadata|--enable-compact-entries)
                        printf "aapt2-wrapper: dropping unsupported flag %s\n" "$1" >&2
                        shift
                        ;;
                    --feature-flags)
                        printf "aapt2-wrapper: dropping unsupported flag %s (and its value)\n" "$1" >&2
                        if [ $# -ge 2 ]; then shift 2; else shift; fi
                        ;;
                    *)
                        args+=("$1")
                        shift
                        ;;
                esac
            done
            exec "$REAL_AAPT2" ${args[@]+"${args[@]}"}

            """;

        File.WriteAllText(wrapperPath, script);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                wrapperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            );
        }

        return wrapperPath;
    }

    // Prepares the system-aapt2 fallback: resolves the system binary (running
    // the apt self-heal when the host has none - the self-heal gate holds,
    // because the only caller is the retry net in Encode, i.e. the bundled
    // aapt2 has just died inside a real build), writes the wrapper and
    // smoke-tests it. Returns the wrapper path when the fallback is usable,
    // null when it is not.
    internal static string? PrepareSystemAapt2Fallback()
    {
        string? systemAapt2 = ResolveSystemAapt2() ?? SelfHealInstallAapt();

        if (systemAapt2 is null)
        {
            return null;
        }

        string wrapperPath = WriteSystemAapt2Wrapper(systemAapt2);

        return ProbeAapt2Compile(wrapperPath).Ok ? wrapperPath : null;
    }

    /// <summary>
    /// Self-heal for hosts where the aapt2 bundled in apktool.jar cannot run
    /// (e.g. SIGILL on the arm64 phone) and NO system aapt2 exists anywhere
    /// (e.g. a stale proot rootfs that predates the Docker image's <c>aapt</c>
    /// package): installs the Debian/Ubuntu <c>aapt</c> package - which ships
    /// a native /usr/bin/aapt2 for the host arch - and re-runs the candidate
    /// search. Returns the newly resolved system aapt2, or null when the
    /// install is impossible (no apt-get, Windows) or failed.
    /// </summary>
    /// <remarks>
    /// STRICT GATE: only ever called after the bundled aapt2 failed its
    /// pre-flight probe (SelectAapt2) or died inside a real build (the retry
    /// net in Encode through PrepareSystemAapt2Fallback) AND the candidate
    /// search came up empty - on hosts where the bundled binary works (x86
    /// dev machines, Docker - which additionally always sets AAPT2_PATH so
    /// no probe runs at all, healthy Raspberry Pis) this never executes, and
    /// a rootfs that already ships <c>aapt</c> resolves a system candidate
    /// before it. Both apt-get commands run with a hard timeout, log only at
    /// Debug level and can never fail the job by themselves. Attempted at
    /// most once per process lifetime (see <c>selfHealAttempted</c>).
    /// </remarks>
    internal static string? SelfHealInstallAapt()
    {
        if (OperatingSystem.IsWindows() || !File.Exists(AptGetPath))
        {
            return null; // no apt here (macOS, Alpine, Windows, ...) - nothing to self-heal with
        }

        if (Interlocked.Exchange(ref selfHealAttempted, 1) is not 0)
        {
            return null; // one attempt per process lifetime is enough
        }

        Log.Warning(
            "aapt2: the bundled aapt2 is broken on this host and no system aapt2 was found - attempting to self-heal by installing the 'aapt' package (this can take a minute)");

        RunAptGetQuiet("update");
        RunAptGetQuiet("install", "-y", "aapt");

        string? resolved = ResolveSystemAapt2();

        if (resolved is null)
        {
            Log.Debug("aapt2 self-heal: still no usable system aapt2 after the install attempt");
        }
        else
        {
            Log.Debug($"aapt2 self-heal: system aapt2 now available at '{resolved}'");
        }

        return resolved;
    }

    // Runs one apt-get command of the self-heal path (see SelfHealInstallApt)
    // with a hard timeout, logging every output line (apt is chatty on both
    // streams) at Debug level only - a self-heal attempt must never spam the
    // job log at Error level. Never throws: any failure (no root, no network,
    // missing sources, timeout) just returns false and the caller re-scans as
    // if nothing had happened.
    private static bool RunAptGetQuiet(params string[] aptArgs)
    {
        try
        {
            var startInfo = new ProcessStartInfo()
            {
                FileName = AptGetPath,
                WorkingDirectory = "/",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // never prompt (debconf & friends) - there is no interactive user here
            startInfo.EnvironmentVariables["DEBIAN_FRONTEND"] = "noninteractive";

            foreach (string arg in aptArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new Process() { StartInfo = startInfo };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Log.Debug("[apt] " + e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Log.Debug("[apt] " + e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                // same CPU policy as U.Run - never starve the panel / game server
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // best-effort
            }

            int? exitCode = WaitForExitOrKill(process, SelfHealTimeoutMilliseconds);

            if (exitCode is not 0)
            {
                Log.Debug($"[apt] {AptGetPath} {string.Join(' ', aptArgs)} -> {(exitCode is null ? $"killed after the {SelfHealTimeoutMilliseconds / 1000}s timeout" : $"exit={exitCode}")}");
            }

            return exitCode is 0;
        }
        catch
        {
            // NEVER fatal - the self-heal is strictly best-effort
            return false;
        }
    }

    // Resets the once-per-process self-heal gate (stub harness only).
    internal static void ResetSelfHealGateForTests()
    {
        selfHealAttempted = 0;
    }

    /// <summary>
    /// Real-compile smoke test for an aapt2 candidate binary (bundled or
    /// system): builds a minimal resources tree and runs the exact resource
    /// command apktool 3.0.3 runs during every build -
    /// <c>aapt2 compile --dir &lt;dir&gt;/res --legacy --no-crunch -o &lt;dir&gt;/out.zip</c>.
    /// The candidate passes iff it exits 0 AND produces the output zip.
    /// </summary>
    /// <remarks>
    /// A <c>version</c> call is NOT a valid pre-flight: on the phone
    /// (Termux/proot arm64) the bundled aapt2 answers <c>version</c> with exit
    /// 0 - a trivial code path - and dies with SIGILL (exit code 132) only on
    /// a real compile, which made the version-based probe a false negative.
    /// A candidate that hangs is killed after
    /// <see cref="ProbeTimeoutMilliseconds"/> (a hung binary must not hang
    /// the whole job) and the probe directory is always cleaned up.
    /// </remarks>
    internal static (bool Ok, int ExitCode) ProbeAapt2Compile(string binaryPath)
    {
        string probeDir = Path.Combine(Path.GetTempPath(), $"aapt2-smoke-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(probeDir, "res", "values"));
            File.WriteAllText(
                Path.Combine(probeDir, "res", "values", "strings.xml"),
                "<resources><string name=\"apace_aapt2_probe\">ok</string></resources>"
            );

            string outputZip = Path.Combine(probeDir, "out.zip");

            var process = U.Run(
                binaryPath,
                probeDir,
                "compile", "--dir", Path.Combine(probeDir, "res"), "--legacy", "--no-crunch", "-o", outputZip
            );

            int? exitCode = WaitForExitOrKill(process, ProbeTimeoutMilliseconds);

            if (exitCode is null)
            {
                return (false, ProbeTimedOut);
            }

            if (exitCode is not 0)
            {
                return (false, exitCode.Value);
            }

            // aapt2 exiting 0 without producing the zip is broken too; report
            // it distinctly so the diagnostics stay meaningful
            return File.Exists(outputZip) ? (true, 0) : (false, ProbeNoOutput);
        }
        catch
        {
            // the candidate did not even start (e.g. ENOEXEC for a foreign-arch
            // ELF) - apktool would fail the same way
            return (false, ProbeFailedToStart);
        }
        finally
        {
            try
            {
                Directory.Delete(probeDir, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    // Waits for `process` to exit within `timeoutMilliseconds` and returns
    // its exit code, or null when it had to be killed (best-effort,
    // entireProcessTree - a wedged candidate must never hang a patch job)
    // after the timeout. The no-argument WaitForExit after a successful
    // timed wait additionally flushes U.Run's async output handlers - needed
    // wherever their captured output is read (e.g. FindAapt2OnPath). Never
    // throws; disposes the process either way.
    private static int? WaitForExitOrKill(Process process, int timeoutMilliseconds)
    {
        try
        {
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
                catch
                {
                    // best-effort kill
                }

                return null;
            }

            process.WaitForExit(); // flush the async output handlers

            return process.ExitCode;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                process.Close();
                process.Dispose();
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    // Matches the process output of a failed apktool build when the aapt2
    // binary itself could not be executed (rather than a resource error):
    // apktool's "Execution failed (exit code = NNN)" (from brut.util.OS.exec)
    // with NNN >= 126 - 126/127 mean could-not-execute, 128+n means killed by
    // signal n (e.g. 132 = SIGILL) - or the JVM's "Cannot run program
    // \"...aapt2...\"" when the binary did not start at all (ENOEXEC for a
    // foreign-arch ELF). Resource errors (exit codes below 126) never match
    // and must surface unchanged. Internal static so tests can feed realistic
    // apktool output via reflection.
    internal static bool IsAapt2ExecFailure(string processOutput, out int aapt2ExitCode)
    {
        var match = Regex.Match(processOutput, @"Execution failed \(exit code = (\d+)\)");

        if (match.Success && int.TryParse(match.Groups[1].Value, out int exitCode) && exitCode >= 126)
        {
            aapt2ExitCode = exitCode;
            return true;
        }

        if (Regex.IsMatch(processOutput, @"Cannot run program ""[^""]*aapt2"))
        {
            aapt2ExitCode = -1; // never started, so there is no exit code to report
            return true;
        }

        aapt2ExitCode = 0;
        return false;
    }
}