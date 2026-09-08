using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Apace.LauncherUI.Controllers;

/// <summary>
/// Read-only file access to the server's data directories, plus editing of a tiny
/// allow-list of text configuration files. Anything that is not on the allow-list is
/// download-only, and every path is resolved against a fixed root with a traversal
/// and symlink-escape guard.
/// </summary>
[ApiController]
[Authorize(Policy = Permissions.ViewFiles)]
[Route("api/files")]
internal sealed class FilesController : ControllerBase
{
    /// <summary>Maximum number of characters returned by the view endpoint (the file is truncated beyond it).</summary>
    internal const long MaxViewBytes = 512 * 1024;

    /// <summary>Matches the [RequestSizeLimit] of the save endpoint.</summary>
    internal const int MaxSaveBytes = 2_000_000;

    private const int MaxBackupsPerFile = 3;

    // The panel's own working directory (config.json lives there). Program.cs resolves its
    // other roots (../components, ../staticdata, ../data) against this same directory, and
    // PatcherService can change the working directory mid-flight, so it is captured once
    // at startup like the rest of them.
    private static readonly string LauncherDir = Path.GetFullPath(".");

    private static readonly Dictionary<string, string> RootDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["launcher"] = LauncherDir,
        ["components"] = Program.ProgramsDir,
        ["staticdata"] = Program.StaticDataDir,
        ["data"] = Program.DataDir,
        ["persistent_fabric"] = Program.PersistentFabricDir,
    };

    // Editing is limited to exact (root, relative path) pairs. api_config.json contains
    // secrets, so only owners with files.edit ever see it editable. config.json is a
    // bind-mounted FILE in docker (no rename-into-place), so saves rewrite it in place.
    private static readonly HashSet<string> EditableFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher|config.json",
        "components|api_config.json",
        "persistent_fabric|server.properties",
        "staticdata|server_template_dir/eula.txt",
    };

    // Viewing is for text configs; these extensions are always binaries here (jars,
    // databases, images, native libraries...). They stay downloadable.
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jar", ".db", ".zip", ".png", ".jpg", ".jpeg", ".gif", ".so", ".dll", ".exe", ".bin", ".ico", ".pdf", ".7z", ".gz", ".woff", ".woff2", ".ttf",
    };

    /// <summary>Single source of truth for which files the Files page offers an editor for.</summary>
    internal static bool IsEditable(string root, string relativePath)
        => EditableFiles.Contains($"{root}|{Normalize(relativePath)}");

    [HttpGet("list")]
    public IActionResult List([FromQuery] string? root, [FromQuery] string? path)
    {
        if (!TryResolvePath(root, path, out var fullPath, out var error))
        {
            return BadRequest(error);
        }

        if (!Directory.Exists(fullPath))
        {
            return System.IO.File.Exists(fullPath)
                ? BadRequest("The requested path is a file, not a directory.")
                : NotFound($"Directory does not exist: {fullPath}");
        }

        try
        {
            var entries = new List<FileEntry>();

            foreach (var fileSystemEntry in Directory.EnumerateFileSystemEntries(fullPath))
            {
                var info = new FileInfo(fileSystemEntry);
                var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                entries.Add(new FileEntry(
                    Path.GetFileName(fileSystemEntry),
                    isDir,
                    isDir ? 0 : info.Length,
                    info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            }

            entries.Sort(static (a, b) =>
            {
                var directoryCompare = b.IsDir.CompareTo(a.IsDir);
                return directoryCompare != 0 ? directoryCompare : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return Ok(entries);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing directory failed: {Directory}", fullPath);
            return Problem($"Could not list the directory: {ex.Message}");
        }
    }

    [HttpGet("view")]
    public IActionResult View([FromQuery] string? root, [FromQuery] string? path)
    {
        if (!TryResolvePath(root, path, out var fullPath, out var error))
        {
            return BadRequest(error);
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound(Directory.Exists(fullPath) ? "The requested path is a directory." : "File does not exist.");
        }

        var extension = Path.GetExtension(fullPath);
        if (BinaryExtensions.Contains(extension))
        {
            return BadRequest($"'{Path.GetFileName(fullPath)}' is a binary file ({extension}) - use download instead.");
        }

        try
        {
            // FileShare.ReadWrite: config files are written by the running services.
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var buffer = new char[MaxViewBytes];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var content = new string(buffer, 0, read);
            var truncated = reader.Peek() != -1;

            if (content.Contains('\0'))
            {
                return BadRequest($"'{Path.GetFileName(fullPath)}' does not look like a text file - use download instead.");
            }

            return Ok(new ViewResultDto(content, truncated, new FileInfo(fullPath).Length, fullPath));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading file failed: {File}", fullPath);
            return Problem($"Could not read the file: {ex.Message}");
        }
    }

    [HttpGet("download")]
    public IActionResult Download([FromQuery] string? root, [FromQuery] string? path)
    {
        if (!TryResolvePath(root, path, out var fullPath, out var error))
        {
            return BadRequest(error);
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound(Directory.Exists(fullPath) ? "The requested path is a directory." : "File does not exist.");
        }

        // Same reasoning as PatcherController.Download: stream immediately, let the
        // browser resume on hiccups and keep reverse proxies from buffering the body.
        Response.Headers.Append("X-Accel-Buffering", "no");

        return PhysicalFile(fullPath, "application/octet-stream", fileDownloadName: Path.GetFileName(fullPath), enableRangeProcessing: true);
    }

    [HttpPost("save")]
    [Authorize(Policy = Permissions.EditFiles)]
    [RequestSizeLimit(MaxSaveBytes)]
    public async Task<IActionResult> Save([FromQuery] string? root, [FromQuery] string? path, [FromBody] SaveRequestDto? request, CancellationToken cancellationToken)
    {
        if (!TryResolvePath(root, path, out var fullPath, out var error))
        {
            return BadRequest(error);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("A relative file path is required.");
        }

        if (!IsEditable(root!, path))
        {
            return BadRequest("This file is not editable through the panel - editing is limited to a small allow-list of configuration files.");
        }

        if (request is null || request.Content is null)
        {
            return BadRequest("Missing 'content' in the request body.");
        }

        try
        {
            var backupName = CreateBackup(fullPath);

            // Written in place on purpose: config.json is a bind-mounted FILE in docker,
            // so the temp-file + File.Move dance other configs can use would break it
            // (same approach as Settings.SaveAsync).
            await System.IO.File.WriteAllTextAsync(fullPath, request.Content, Encoding.UTF8, cancellationToken);

            Log.Information("File saved through the panel: {File} (backup: {Backup}, {Bytes} bytes)",
                fullPath, backupName ?? "none", request.Content.Length);

            return Ok(new SaveResultDto(backupName, new FileInfo(fullPath).Length));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving file failed: {File}", fullPath);
            return Problem($"Could not save the file: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the current file to "&lt;name&gt;.bak-yyyyMMddHHmmss" next to it and prunes
    /// older backups so only the newest <see cref="MaxBackupsPerFile"/> remain.
    /// Returns the backup file name, or null when the file does not exist yet.
    /// </summary>
    private static string? CreateBackup(string fullPath)
    {
        var file = new FileInfo(fullPath);

        if (!file.Exists)
        {
            return null;
        }

        var prefix = file.Name + ".bak-";
        var backupPath = Path.Combine(file.Directory!.FullName, prefix + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));

        file.CopyTo(backupPath, overwrite: true);

        foreach (var stale in file.Directory
                     .EnumerateFiles(prefix + "*")
                     .Where(candidate => candidate.Name.StartsWith(prefix, StringComparison.Ordinal))
                     .OrderByDescending(candidate => candidate.Name, StringComparer.Ordinal)
                     .Skip(MaxBackupsPerFile))
        {
            try
            {
                stale.Delete();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not delete stale backup: {File}", stale.FullName);
            }
        }

        return Path.GetFileName(backupPath);
    }

    /// <summary>
    /// Maps a root name plus an untrusted relative path onto a path that is guaranteed
    /// to stay inside the root: absolute inputs and '..' are rejected up front, the
    /// combination is fully re-resolved and re-checked, and every path segment is
    /// resolved through symlinks so a link cannot point outside the root.
    /// </summary>
    private static bool TryResolvePath(string? root, string? relativePath, out string fullPath, out string? error)
    {
        fullPath = "";
        error = null;

        if (string.IsNullOrWhiteSpace(root))
        {
            error = "The 'root' query parameter is required.";
            return false;
        }

        if (!RootDirectories.TryGetValue(root, out var rootDirectory))
        {
            error = $"Unknown root '{root}'. Valid roots: {string.Join(", ", RootDirectories.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase))}.";
            return false;
        }

        relativePath = Normalize(relativePath ?? "");

        if (relativePath.Contains('\0'))
        {
            error = "Path contains an invalid character.";
            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            error = "Absolute paths are not allowed - use a path relative to the selected root.";
            return false;
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Contains(".."))
        {
            error = "'..' is not allowed - use a path relative to the selected root.";
            return false;
        }

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine([rootDirectory, .. segments]));
        }
        catch (Exception ex)
        {
            error = $"Invalid path: {ex.Message}";
            return false;
        }

        if (!IsInsideRoot(combined, rootDirectory))
        {
            error = "Path escapes the selected root.";
            return false;
        }

        // A symlink anywhere on the way (directory in the middle or the file itself)
        // must not be able to lead outside the root.
        try
        {
            var probe = rootDirectory;
            foreach (var segment in segments)
            {
                probe = Path.Combine(probe, segment);

                if (ResolveLinkTarget(probe) is { } linkTarget && !IsInsideRoot(linkTarget, rootDirectory))
                {
                    error = "Path escapes the selected root through a symbolic link.";
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            error = $"Could not resolve the path: {ex.Message}";
            return false;
        }

        fullPath = combined;
        return true;
    }

    private static string? ResolveLinkTarget(string path)
    {
        FileSystemInfo? info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

        // Missing paths (a config file that does not exist yet) have no link target;
        // without this check ResolveLinkTarget would throw FileNotFoundException.
        if (!info.Exists)
        {
            return null;
        }

        try
        {
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // deleted between the Exists check and the resolution
            return null;
        }
    }

    private static bool IsInsideRoot(string path, string rootDirectory)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(full, rootDirectory, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(rootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/');

    internal sealed record FileEntry(string Name, bool IsDir, long Size, string Mtime);

    internal sealed record ViewResultDto(string Content, bool Truncated, long Size, string FullPath);

    internal sealed record SaveRequestDto(string? Content);

    internal sealed record SaveResultDto(string? BackupName, long Size);
}
