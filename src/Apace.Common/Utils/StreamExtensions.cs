namespace Apace.Common.Utils;

public static class StreamExtensions
{
    /// <summary>
    /// Reads a JSON body as <typeparamref name="T"/>; a body that is not valid JSON
    /// (or is empty) answers null instead of throwing — the controllers treat null
    /// as a bad request, so a client posting a truncated/garbage payload to an
    /// endpoint that reads Request.Body by hand gets a clean 400, not an empty 500.
    /// </summary>
    public static async ValueTask<T?> AsJsonAsync<T>(this Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await Json.DeserializeAsync<T>(stream, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
    }

    public static async Task<string> ReadAsString(this Stream stream, CancellationToken cancellationToken = default)
    {
        using (var reader = new StreamReader(stream))
        {
            return await reader.ReadToEndAsync(cancellationToken);
        }
    }
}
