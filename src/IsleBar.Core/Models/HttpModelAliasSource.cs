namespace IsleBar.Core.Models;

/// <summary>
/// Fetches the official docs over HTTP. <b>A User-Agent header is required</b> — without it you get 403.
/// All failures return <c>null</c> (the update is skipped and retried next time).
/// </summary>
public sealed class HttpModelAliasSource : IModelAliasSource, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _url;

    public HttpModelAliasSource(HttpClient? client = null, string? url = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _url = new Uri(url ?? ModelAliasParser.DocumentUrl);
    }

    public async Task<string?> FetchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.TryAddWithoutValidation("User-Agent", ModelAliasParser.UserAgent);
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
