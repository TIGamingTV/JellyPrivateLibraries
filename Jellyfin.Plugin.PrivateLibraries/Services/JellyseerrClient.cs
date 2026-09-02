using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PrivateLibraries.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PrivateLibraries.Services;

/// <summary>
/// Minimal read-only client for the Jellyseerr REST API. Only what the backfill sync
/// needs: a status probe and a paged read of every request.
/// </summary>
/// <remarks>
/// Deliberately built on <see cref="System.Net.Http.HttpClient"/> and
/// <see cref="System.Text.Json"/> only. The release workflow packages a single DLL with no
/// dependency-copying step, so a NuGet HTTP/JSON library would break every release.
/// </remarks>
public class JellyseerrClient
{
    /// <summary>
    /// Page size for <c>GET /api/v1/request</c>. Jellyseerr defaults to 20; 100 keeps the
    /// number of round trips low without asking the server for an unbounded page.
    /// </summary>
    private const int PageSize = 100;

    /// <summary>
    /// Hard ceiling on pages fetched, so a server that ignores <c>skip</c> and keeps
    /// returning the first page cannot spin this into an infinite loop.
    /// </summary>
    private const int MaxPages = 500;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,

        // Jellyseerr emits the provider ids as JSON numbers, but a reverse proxy or a future
        // schema change quoting them should not fail the whole sync.
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JellyseerrClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyseerrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory provided by the server.</param>
    /// <param name="logger">The logger.</param>
    public JellyseerrClient(IHttpClientFactory httpClientFactory, ILogger<JellyseerrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Gets a value indicating whether both the Jellyseerr URL and API key are configured.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Config.JellyseerrUrl)
        && !string.IsNullOrWhiteSpace(Config.JellyseerrApiKey);

    /// <summary>
    /// Normalizes the configured base URL into the API root, tolerating a trailing slash and
    /// a URL the admin already suffixed with "/api/v1".
    /// </summary>
    /// <param name="configuredUrl">The URL from the plugin configuration.</param>
    /// <returns>The API root without a trailing slash.</returns>
    /// <exception cref="InvalidOperationException">The URL is missing or unparseable.</exception>
    public static string BuildApiRoot(string configuredUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            throw new InvalidOperationException("The Jellyseerr URL is not configured.");
        }

        var trimmed = configuredUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "The Jellyseerr URL must be an absolute http(s) URL, for example http://jellyseerr:5055.");
        }

        if (trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed + "/api/v1";
    }

    /// <summary>
    /// Probes the Jellyseerr instance and verifies the API key is accepted.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The reported Jellyseerr version.</returns>
    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        // /status is unauthenticated, so it only proves the URL is right. Reading a single
        // request page is what actually exercises the API key and its permissions.
        var status = await GetAsync<JellyseerrStatus>("/status", cancellationToken).ConfigureAwait(false);
        await GetAsync<JellyseerrRequestPage>("/request?take=1&skip=0&filter=all&sort=added", cancellationToken)
            .ConfigureAwait(false);

        return status?.Version ?? "unknown";
    }

    /// <summary>
    /// Reads every request from Jellyseerr, oldest first, following the paging metadata.
    /// </summary>
    /// <param name="progress">Optional progress reporter over the 0-100 range.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Every request the API key is allowed to see.</returns>
    public async Task<IReadOnlyList<JellyseerrRequest>> GetAllRequestsAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var all = new List<JellyseerrRequest>();
        var seenIds = new HashSet<int>();

        for (var page = 0; page < MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = string.Format(
                CultureInfo.InvariantCulture,
                "/request?take={0}&skip={1}&filter=all&sort=added",
                PageSize,
                page * PageSize);

            var result = await GetAsync<JellyseerrRequestPage>(path, cancellationToken).ConfigureAwait(false);
            var results = result?.Results;
            if (results is null || results.Count == 0)
            {
                break;
            }

            var newOnThisPage = 0;
            foreach (var request in results)
            {
                if (seenIds.Add(request.Id))
                {
                    all.Add(request);
                    newOnThisPage++;
                }
            }

            // A server that ignores "skip" would hand back the first page forever; every id
            // already being known is the tell, so stop instead of looping to MaxPages.
            if (newOnThisPage == 0)
            {
                _logger.LogWarning(
                    "Jellyseerr returned no new requests on page {Page}; stopping paging early", page + 1);
                break;
            }

            var total = result?.PageInfo?.Results ?? 0;
            if (total > 0)
            {
                progress?.Report(Math.Min(100d, all.Count * 100d / total));
            }

            if (results.Count < PageSize)
            {
                break;
            }
        }

        _logger.LogInformation("Read {Count} request(s) from Jellyseerr", all.Count);
        return all;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        var apiKey = Config.JellyseerrApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("The Jellyseerr API key is not configured.");
        }

        var url = BuildApiRoot(Config.JellyseerrUrl) + path;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey.Trim());
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Translate the two failures an admin will actually hit into actionable text
            // rather than surfacing a bare status code in the configuration page.
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Jellyseerr rejected the API key (HTTP " + (int)response.StatusCode
                    + "). Copy it from Jellyseerr → Settings → General → API Key.",
                HttpStatusCode.NotFound =>
                    "Jellyseerr returned 404 for " + url
                    + ". Check the Jellyseerr URL points at the site root.",
                _ => "Jellyseerr request to " + url + " failed with HTTP " + (int)response.StatusCode + "."
            };

            throw new InvalidOperationException(message);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer
                .DeserializeAsync<T>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
