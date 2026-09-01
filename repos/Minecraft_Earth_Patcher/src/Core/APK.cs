using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MCEPatcher.Core;

public static class APK
{
    public const string FileName = "apktool.jar";
    public const string FileNameBat = "apktool.bat";

    public static bool Decode(FileInfo apk, DirectoryInfo output)
    {
        Process process;
        if (OperatingSystem.IsWindows() && File.Exists(FileNameBat))
        {
            process = U.Run(Path.GetFullPath(FileNameBat), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            [
                "d",
                "-f",
                "-o", $"\"{output.FullName}\"",
                $"\"{apk.FullName}\""
            ]);
        }
        else
        {
            process = U.Run("java", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            [
                "-jar", $"\"{Path.GetFullPath(FileName)}\"",
                "d",
                "-f",
                "-o", $"\"{output.FullName}\"",
                $"\"{apk.FullName}\""
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

        List<string> args =
        [
            "b",
            "-f",
            "-o", $"\"{outApk.FullName}\"",
        ];

        // apktool 3.x bundles an x86-64 aapt2 only; on other hosts (e.g. ARM64
        // servers) the bundled binary cannot be executed, so allow overriding
        // it with a native one from the system (AAPT2_PATH, see SystemTools).
        if (SystemTools.Aapt2Path is { } aapt2)
        {
            args.Add("--aapt");
            args.Add($"\"{aapt2}\"");
        }

        args.Add($"\"{input.FullName}\"");

        Process process;
        if (OperatingSystem.IsWindows() && File.Exists(FileNameBat))
        {
            process = U.Run(Path.GetFullPath(FileNameBat), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), [.. args]);
        }
        else
        {
            process = U.Run("java", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            [
                "-jar", $"\"{Path.GetFullPath(FileName)}\"",
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
}
