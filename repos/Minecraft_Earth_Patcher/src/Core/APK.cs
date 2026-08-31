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

        Process process;
        if (OperatingSystem.IsWindows() && File.Exists(FileNameBat))
        {
            process = U.Run(Path.GetFullPath(FileNameBat), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            [
                "b",
                "-f",
                "-o", $"\"{outApk.FullName}\"",
                $"\"{input.FullName}\""
            ]);
        }
        else
        {
            process = U.Run("java", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            [
                "-jar", $"\"{Path.GetFullPath(FileName)}\"",
                "b",
                "-f",
                "-o", $"\"{outApk.FullName}\"",
                $"\"{input.FullName}\""
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
