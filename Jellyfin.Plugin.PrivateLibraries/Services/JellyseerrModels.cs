using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.PrivateLibraries.Services;

/// <summary>
/// Request lifecycle states returned by Jellyseerr (<c>server/constants/media.ts</c>).
/// Kept as named constants so the sync's filtering reads the same way the webhook's
/// notification-type filtering does.
/// </summary>
public static class JellyseerrRequestStatus
{
    /// <summary>Awaiting manual approval.</summary>
    public const int Pending = 1;

    /// <summary>Approved (manually or automatically).</summary>
    public const int Approved = 2;

    /// <summary>Declined by an administrator.</summary>
    public const int Declined = 3;

    /// <summary>The downstream service failed to accept the request.</summary>
    public const int Failed = 4;

    /// <summary>Approved and fully delivered.</summary>
    public const int Completed = 5;
}

/// <summary>
/// One page of <c>GET /api/v1/request</c>.
/// </summary>
public class JellyseerrRequestPage
{
    /// <summary>Gets or sets the paging metadata.</summary>
    [JsonPropertyName("pageInfo")]
    public JellyseerrPageInfo? PageInfo { get; set; }

    /// <summary>Gets or sets the requests on this page.</summary>
    [JsonPropertyName("results")]
    public List<JellyseerrRequest> Results { get; set; } = new();
}

/// <summary>
/// Paging metadata block.
/// </summary>
public class JellyseerrPageInfo
{
    /// <summary>Gets or sets the current page number (1-based).</summary>
    [JsonPropertyName("page")]
    public int Page { get; set; }

    /// <summary>Gets or sets the total number of pages.</summary>
    [JsonPropertyName("pages")]
    public int Pages { get; set; }

    /// <summary>Gets or sets the total number of results across all pages.</summary>
    [JsonPropertyName("results")]
    public int Results { get; set; }
}

/// <summary>
/// A single Jellyseerr media request.
/// </summary>
public class JellyseerrRequest
{
    /// <summary>Gets or sets the request id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the request status. See <see cref="JellyseerrRequestStatus"/>.
    /// </summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }

    /// <summary>Gets or sets the media the request refers to.</summary>
    [JsonPropertyName("media")]
    public JellyseerrMediaInfo? Media { get; set; }

    /// <summary>Gets or sets the user who made the request.</summary>
    [JsonPropertyName("requestedBy")]
    public JellyseerrUser? RequestedBy { get; set; }
}

/// <summary>
/// The media block of a request, carrying the external provider ids the plugin grants on.
/// </summary>
public class JellyseerrMediaInfo
{
    /// <summary>Gets or sets the media type ("movie" or "tv").</summary>
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("tmdbId")]
    public long? TmdbId { get; set; }

    /// <summary>Gets or sets the TVDB id (null for movies).</summary>
    [JsonPropertyName("tvdbId")]
    public long? TvdbId { get; set; }

    /// <summary>Gets or sets the availability status of the media.</summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }
}

/// <summary>
/// The requesting Jellyseerr account. Which identity fields are populated depends on how
/// the Jellyseerr instance authenticates its users, so the sync tries each in turn.
/// </summary>
public class JellyseerrUser
{
    /// <summary>Gets or sets the Jellyseerr-internal user id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the linked Jellyfin user id. Present when Jellyseerr is backed by
    /// Jellyfin; this is the only unambiguous mapping key, so it is tried first.
    /// </summary>
    [JsonPropertyName("jellyfinUserId")]
    public string? JellyfinUserId { get; set; }

    /// <summary>Gets or sets the linked Jellyfin username.</summary>
    [JsonPropertyName("jellyfinUsername")]
    public string? JellyfinUsername { get; set; }

    /// <summary>Gets or sets the local Jellyseerr username.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Gets or sets the display name Jellyseerr computes for the account.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the account email (used for logging only).</summary>
    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

/// <summary>
/// Response of <c>GET /api/v1/status</c>, used by the connection test.
/// </summary>
public class JellyseerrStatus
{
    /// <summary>Gets or sets the Jellyseerr version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
