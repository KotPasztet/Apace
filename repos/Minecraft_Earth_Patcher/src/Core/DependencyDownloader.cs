using Serilog;

namespace MCEPatcher.Core;

public static class DependencyDownloader
{
    private static readonly HttpClient client = new HttpClient();

    public static async Task Download(string url, string fileName)
    {
        // A previously interrupted download (killed process, container
        // restart mid-write, disk full, etc.) can leave a 0-byte or
        // truncated file under `fileName`. File.Exists alone can't tell a
        // complete dependency from a corrupt leftover, and a corrupt file
        // sitting in the shared cache would silently break every future
        // job forever (they'd all see "exists" and skip re-downloading).
        // Treat a zero-length file as "not downloaded" and remove it.
        if (File.Exists(fileName))
        {
            if (new FileInfo(fileName).Length > 0)
            {
                return;
            }

            Log.Warning($"Found empty/corrupt {fileName} in cache; re-downloading.");
            File.Delete(fileName);
        }

        Log.Debug($"Downloading {fileName}...");

        // Download to a temp file first and only move it into place once
        // fully written. This makes the operation atomic from the point of
        // view of any concurrent job / File.Exists check: readers either
        // see no file, or a fully-downloaded one, never a partial one.
        var tempFileName = fileName + $".download-{Guid.NewGuid():N}.tmp";

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var downloadStream = await response.Content.ReadAsStreamAsync();

            using (var fileStream = new FileStream(tempFileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[16 * 1024]; // 16KB chunks
                long totalRead = 0;
                int bytesRead;

                int lastPercentDownloaded = -1;

                while ((bytesRead = await downloadStream.ReadAsync(buffer, 0, buffer.Length)) is not 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    int percentDownloaded = (int)(((double)totalRead / totalBytes) * 100);

                    if (totalBytes is not -1 && percentDownloaded != lastPercentDownloaded)
                    {
                        UpdateProgressBar(totalRead, totalBytes);
                        lastPercentDownloaded = percentDownloaded;
                    }
                }
            }

            File.Move(tempFileName, fileName, overwrite: true);

            Log.Information($"Downloaded {fileName}");
        }
        finally
        {
            // Best-effort cleanup: if we crashed/threw partway through, the
            // temp file (not the real fileName) is what's left behind, so
            // the next run's File.Exists(fileName) check is unaffected.
            if (File.Exists(tempFileName))
            {
                try
                {
                    File.Delete(tempFileName);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    private static void UpdateProgressBar(long current, long total)
    {
        const int ProgressBarWidth = 30;
        var percent = (double)current / total;
        var filledWidth = (int)(percent * ProgressBarWidth);

        var bar = new string('#', filledWidth) + new string('-', ProgressBarWidth - filledWidth);

        Log.Debug($"[{bar}] {percent:P0} ");
    }
}