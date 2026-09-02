using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PrivateLibraries.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PrivateLibraries.Services;

/// <summary>
/// Backfills grants from the requests already stored in Jellyseerr.
/// </summary>
/// <remarks>
/// The webhook only ever reports activity that happens *after* the plugin is installed and
/// configured, so every request made before that point is invisible to it — a user who
/// switches restriction on then loses access to their entire request history. This walks the
/// Jellyseerr REST API and creates the same grants the webhook would have created, using the
/// same identity fields (<see cref="GrantEntry.ProviderName"/>/<see cref="GrantEntry.ProviderId"/>
/// and <c>Source = "Seerr"</c>) so the two paths dedupe against each other.
/// </remarks>
public class JellyseerrSyncService
{
    private readonly JellyseerrClient _client;
    private readonly RestrictionManager _restrictionManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<JellyseerrSyncService> _logger;

    // A scheduled run and the configuration page's "Sync now" button can fire at the same
    // time. Both would produce the same grants, but they would also race each other's
    // read-modify-write of the configuration, so only one runs at a time.
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyseerrSyncService"/> class.
    /// </summary>
    /// <param name="client">The Jellyseerr API client.</param>
    /// <param name="restrictionManager">The restriction manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="logger">The logger.</param>
    public JellyseerrSyncService(
        JellyseerrClient client,
        RestrictionManager restrictionManager,
        IUserManager userManager,
        ILogger<JellyseerrSyncService> logger)
    {
        _client = client;
        _restrictionManager = restrictionManager;
        _userManager = userManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Reads every Jellyseerr request and grants the ones that map to a Jellyfin user.
    /// </summary>
    /// <param name="progress">Optional progress reporter over the 0-100 range.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A summary of what the sync did.</returns>
    /// <exception cref="InvalidOperationException">Jellyseerr is not configured, or a sync is already running.</exception>
    public async Task<JellyseerrSyncResult> SyncAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!_client.IsConfigured)
        {
            throw new InvalidOperationException(
                "Set the Jellyseerr URL and API key on the plugin's configuration page (and save) before syncing.");
        }

        if (!await _syncLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A Jellyseerr sync is already running.");
        }

        try
        {
            return await SyncCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Verifies the configured URL and API key by calling Jellyseerr.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The reported Jellyseerr version.</returns>
    public Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        return _client.TestConnectionAsync(cancellationToken);
    }

    /// <summary>
    /// Whether a request in this state should produce a grant. Mirrors the webhook, which
    /// only grants on MEDIA_APPROVED / MEDIA_AUTO_APPROVED / MEDIA_AVAILABLE: declined and
    /// failed requests never become watchable, and pending ones are opt-in.
    /// </summary>
    private static bool ShouldGrant(int status, bool includePending)
    {
        return status switch
        {
            JellyseerrRequestStatus.Approved or JellyseerrRequestStatus.Completed => true,
            JellyseerrRequestStatus.Pending => includePending,
            _ => false
        };
    }

    private async Task<JellyseerrSyncResult> SyncCoreAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var includePending = Config.JellyseerrSyncIncludePending;
        var result = new JellyseerrSyncResult();

        // Fetching is the first half of the progress bar, applying grants the second.
        var fetchProgress = progress is null
            ? null
            : new Progress<double>(p => progress.Report(p / 2d));

        var requests = await _client.GetAllRequestsAsync(fetchProgress, cancellationToken).ConfigureAwait(false);
        result.RequestsRead = requests.Count;

        // Resolving a Jellyseerr account to a Jellyfin user is the expensive, failure-prone
        // step, so cache it per Jellyseerr user id across that user's whole request history.
        var userCache = new Dictionary<int, Guid?>();
        var unmatched = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var toGrant = new List<SeerrGrantRequest>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ShouldGrant(request.Status, includePending))
            {
                result.RequestsSkippedByStatus++;
                continue;
            }

            var requester = request.RequestedBy;
            if (requester is null)
            {
                result.RequestsWithoutUser++;
                continue;
            }

            if (!userCache.TryGetValue(requester.Id, out var userId))
            {
                userId = ResolveJellyfinUser(requester);
                userCache[requester.Id] = userId;
            }

            if (userId is null)
            {
                result.RequestsWithoutUser++;
                var label = DescribeRequester(requester);
                unmatched[label] = unmatched.TryGetValue(label, out var count) ? count + 1 : 1;
                continue;
            }

            var tmdb = request.Media?.TmdbId;
            var tvdb = request.Media?.TvdbId;
            if (tmdb is null && tvdb is null)
            {
                result.RequestsWithoutProviderId++;
                continue;
            }

            // Same shape as the webhook: a TV request carrying both ids yields two grants,
            // because either provider id may be the one Jellyfin matched the series on.
            AddPair(toGrant, seen, userId.Value, "Tmdb", tmdb);
            AddPair(toGrant, seen, userId.Value, "Tvdb", tvdb);
        }

        result.UnmatchedRequesters = unmatched
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => kvp.Key + " (" + kvp.Value.ToString(CultureInfo.InvariantCulture) + " request(s))")
            .ToList();

        var applyProgress = progress is null
            ? null
            : new Progress<double>(p => progress.Report(50d + (p / 2d)));

        var (created, existed) = await _restrictionManager
            .AddSeerrGrantsAsync(toGrant, applyProgress, cancellationToken)
            .ConfigureAwait(false);

        result.GrantsCreated = created;
        result.GrantsAlreadyPresent = existed;
        progress?.Report(100);

        Config.JellyseerrLastSyncUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        Config.JellyseerrLastSyncSummary = result.Summary;

        // Through the manager so this save is serialized against the reconcile task's, rather
        // than writing the configuration file concurrently with it.
        await _restrictionManager.SaveConfigurationAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Jellyseerr sync finished: {Summary}", result.Summary);
        if (result.UnmatchedRequesters.Count > 0)
        {
            _logger.LogWarning(
                "Jellyseerr sync could not map {Count} requester(s) to a Jellyfin user: {Requesters}",
                result.UnmatchedRequesters.Count,
                string.Join(", ", result.UnmatchedRequesters));
        }

        return result;
    }

    private static void AddPair(
        List<SeerrGrantRequest> target,
        HashSet<string> seen,
        Guid userId,
        string providerName,
        long? providerId)
    {
        if (providerId is null || providerId <= 0)
        {
            return;
        }

        var value = providerId.Value.ToString(CultureInfo.InvariantCulture);

        // One title is commonly requested by the same user more than once (a re-request after
        // a deletion, or per-season TV requests); collapse those before hitting the manager.
        if (!seen.Add(userId.ToString("N", CultureInfo.InvariantCulture) + "|" + providerName + "|" + value))
        {
            return;
        }

        target.Add(new SeerrGrantRequest
        {
            UserId = userId,
            ProviderName = providerName,
            ProviderId = value
        });
    }

    /// <summary>
    /// Maps a Jellyseerr account onto a Jellyfin user. Tries the linked Jellyfin user id
    /// first — the only unambiguous key — then the username fields, because a Jellyseerr
    /// instance that authenticates locally (or against Plex) has no linked id at all.
    /// </summary>
    private Guid? ResolveJellyfinUser(JellyseerrUser requester)
    {
        if (!string.IsNullOrWhiteSpace(requester.JellyfinUserId)
            && Guid.TryParse(requester.JellyfinUserId, out var linkedId)
            && linkedId != Guid.Empty
            && _userManager.GetUserById(linkedId) is not null)
        {
            return linkedId;
        }

        foreach (var name in new[] { requester.JellyfinUsername, requester.Username, requester.DisplayName })
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var user = _userManager.GetUserByName(name);
            if (user is not null)
            {
                return user.Id;
            }
        }

        return null;
    }

    private static string DescribeRequester(JellyseerrUser requester)
    {
        var name = new[] { requester.JellyfinUsername, requester.Username, requester.DisplayName, requester.Email }
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        return name ?? ("Jellyseerr user #" + requester.Id.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Outcome of one Jellyseerr backfill sync.
/// </summary>
public class JellyseerrSyncResult
{
    /// <summary>Gets or sets the number of requests read from Jellyseerr.</summary>
    public int RequestsRead { get; set; }

    /// <summary>Gets or sets the number skipped because they were declined, failed or still pending.</summary>
    public int RequestsSkippedByStatus { get; set; }

    /// <summary>Gets or sets the number whose requester could not be mapped to a Jellyfin user.</summary>
    public int RequestsWithoutUser { get; set; }

    /// <summary>Gets or sets the number that carried neither a TMDB nor a TVDB id.</summary>
    public int RequestsWithoutProviderId { get; set; }

    /// <summary>Gets or sets the number of new grants created.</summary>
    public int GrantsCreated { get; set; }

    /// <summary>Gets or sets the number of grants that already existed.</summary>
    public int GrantsAlreadyPresent { get; set; }

    /// <summary>Gets or sets the Jellyseerr accounts that had no matching Jellyfin user.</summary>
    public IReadOnlyList<string> UnmatchedRequesters { get; set; } = new List<string>();

    /// <summary>Gets a one-line summary suitable for logs and the configuration page.</summary>
    public string Summary => string.Format(
        CultureInfo.InvariantCulture,
        "{0} request(s) read, {1} grant(s) created, {2} already present, {3} skipped by status, {4} with no matching user, {5} with no provider id",
        RequestsRead,
        GrantsCreated,
        GrantsAlreadyPresent,
        RequestsSkippedByStatus,
        RequestsWithoutUser,
        RequestsWithoutProviderId);
}
