using DiffPatch.Data;
using Serilog;
using System.Diagnostics;
using System.Text;

namespace MCEPatcher.Core;

public static class U
{
    // from: https://stackoverflow.com/a/690980/15878562
    public static void CopyDir(string sourceDirectory, string targetDirectory, bool overwrite)
    {
        var diSource = new DirectoryInfo(sourceDirectory);
        var diTarget = new DirectoryInfo(targetDirectory);

        CopyDir(diSource, diTarget, overwrite);
    }

    public static void CopyDir(DirectoryInfo source, DirectoryInfo target, bool overwrite)
    {
        target.Create();

        // Copy each file into the new directory.
        foreach (FileInfo fi in source.GetFiles())
        {
            try
            {
                fi.CopyTo(Path.Combine(target.FullName, fi.Name), overwrite);
            }
            catch
            {
            }
        }

        // Copy each subdirectory using recursion.
        foreach (var diSourceSubDir in source.GetDirectories())
        {
            var nextTargetSubDir = target.CreateSubdirectory(diSourceSubDir.Name);

            CopyDir(diSourceSubDir, nextTargetSubDir, overwrite);
        }
    }

    public static bool IsHex(this FileDiff filePatch)
        => Path.GetExtension(filePatch.From) is ".hexdump" || Path.GetExtension(filePatch.To) is ".hexdump";

    // from: https://stackoverflow.com/a/24016130/15878562
    public static IEnumerable<int> AllIndexesOf(this string str, string searchString)
    {
        var minIndex = str.IndexOf(searchString);

        while (minIndex is not -1)
        {
            yield return minIndex;
            minIndex = str.IndexOf(searchString, minIndex + searchString.Length);
        }
    }

    public static byte[] ReadToEnd(this Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        return ms.ToArray();
    }

    public static Process Run(string file, string workDir, params string[] args)
    {
        var shellExecute = false;

        var startInfo = new ProcessStartInfo()
        {
            FileName = file,
            Arguments = string.Join(' ', args),
            WorkingDirectory = workDir,
            UseShellExecute = shellExecute,
            CreateNoWindow = !shellExecute,
            RedirectStandardOutput = !shellExecute,
            RedirectStandardError = !shellExecute,
            RedirectStandardInput = !shellExecute,
        };
        var process = new Process()
        {
            StartInfo = startInfo,
        };

        if (!shellExecute)
        {
            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Log.Debug("[OUT] " + e.Data);
                }
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Log.Error("[ERR] " + e.Data);
                }
            };
        }

        process.Start();
        if (!shellExecute)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return process;
    }

    public static string ToString(FileDiff diff, bool thisOnly = false)
    {
        var builder = new StringBuilder();

        builder.Append("--- ").AppendLine(diff.From);
        builder.Append("+++ ").AppendLine(diff.To);

        if (!thisOnly)
        {
            foreach (var chunk in diff.Chunks)
            {
                ToString(chunk, builder);
            }
        }

        return builder.ToString();
    }

    public static string ToString(Chunk chunk, bool thisOnly = false)
    {
        var builder = new StringBuilder();
        ToString(chunk, builder, thisOnly);

        return builder.ToString();
    }
    public static void ToString(Chunk chunk, StringBuilder builder, bool thisOnly = false)
    {
        builder.Append("@@ -");
        WriteRange(chunk.RangeInfo.OriginalRange);
        builder.Append("+");
        WriteRange(chunk.RangeInfo.NewRange);
        builder.AppendLine("@@");

        if (!thisOnly)
        {
            foreach (var lineDiff in chunk.Changes)
            {
                ToString(lineDiff, builder);
            }
        }

        void WriteRange(ChunkRange range)
        {
            builder.Append(range.StartLine);
            if (range.LineCount is not 1)
            {
                builder.Append(",").Append(range.LineCount);
            }

            builder.Append(" ");
        }
    }

    public static string ToString(LineDiff diff)
    {
        var builder = new StringBuilder();
        ToString(diff, builder);

        return builder.ToString();
    }
    public static void ToString(LineDiff diff, StringBuilder builder)
    {
        switch (diff.Type)
        {
            case LineChangeType.Add:
                builder.Append("+");
                break;
            case LineChangeType.Delete:
                builder.Append("-");
                break;
            default:
                builder.Append(" ");
                break;
        }

        builder.AppendLine(diff.Content);
    }

    public static void RemoveAt<T>(this IList<T> list, int index, int count)
    {
        for (int i = 0; i < count; i++)
        {
            list.RemoveAt(index);
        }
    }

    public static void AddToEnd<T>(this IList<T> list, T value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            list.Add(value);
        }
    }

    public static void Insert<T>(this IList<T> list, int index, IEnumerable<T> other)
    {
        foreach (T item in other)
        {
            list.Insert(index++, item);
        }
    }

    public static void PAKE()
    {
        if (ApkProcessor.NonInteractive)
        {
            return;
        }

        Console.WriteLine("Press any key to exit...");
        Console.ReadKey(true);
    }

    public static string GetNewLine(string text)
    {
        int index = text.IndexOf('\n');

        if (index < 0)
        {
            if (text.Contains("\r"))
            {
                return "\r";
            }
            else
            {
                return Environment.NewLine;
            }
        }
        else if (index is 0)
        {
            return "\n";
        }

        return text[index - 1] is '\r' ? "\r\n" : "\n";
    }
}
