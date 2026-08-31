using Serilog;

namespace MCEPatcher.Core;

public static class DependencyDownloader
{
    private static readonly HttpClient client = new HttpClient();

    public static async Task Download(string url, string fileName)
    {
        if (File.Exists(fileName))
        {
            return;
        }

        Log.Debug($"Downloading {fileName}...");

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var downloadStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None);

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

        Log.Information($"Downloaded {fileName}");
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