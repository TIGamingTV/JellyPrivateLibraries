using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PrivateLibraries.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PrivateLibraries.Api;

/// <summary>
/// REST API backing the home-screen widget and receiving Jellyseerr webhooks.
/// </summary>
[ApiController]
[Route("PrivateLibraries")]
public class RestrictionController : ControllerBase
{
    private static readonly BaseItemKind[] _grantableKinds = { BaseItemKind.Movie, BaseItemKind.Series };
    private readonly RestrictionManager _restrictionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authContext;
    private readonly ILogger<RestrictionController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RestrictionController"/> class.
    /// </summary>
    /// <param name="restrictionManager">The restriction manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="authContext">The authorization context.</param>
    /// <param name="logger">The logger.</param>
    public RestrictionController(
        RestrictionManager restrictionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IAuthorizationContext authContext,
        ILogger<RestrictionController> logger)
    {
        _restrictionManager = restrictionManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _authContext = authContext;
        _logger = logger;
    }

    /// <summary>
    /// Serves the injected client widget script.
    /// </summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    public ActionResult GetClientScript()
    {
        var assembly = typeof(RestrictionController).Assembly;
        var resource = assembly.GetManifestResourceStream("Jellyfin.Plugin.PrivateLibraries.Web.private-libraries.js");
        if (resource is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(resource, Encoding.UTF8);
        var script = reader.ReadToEnd();
        return Content(script, "application/javascript; charset=utf-8");
    }

    /// <summary>
    /// Gets the current user's restriction status.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Me")]
    [Authorize]
    public async Task<ActionResult<MeStatusDto>> GetMe()
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        return new MeStatusDto
        {
            RestrictionEnabled = _restrictionManager.IsRestrictionEnabled(userId),
            GrantedCount = _restrictionManager.GetGrantedItems(userId).Count
        };
    }

    /// <summary>
    /// Enables or disables the current user's own restriction.
    /// </summary>
    /// <param name="body">The request body.</param>
    /// <returns>No content.</returns>
    [HttpPost("Me/Restriction")]
    [Authorize]
    public async Task<ActionResult> SetRestriction([FromBody] SetRestrictionDto body)
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        try
        {
            await _restrictionManager.SetRestrictionEnabledAsync(userId, body.Enabled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set restriction for user {UserId}", userId);
            return StatusCode(500, new { error = ex.Message });
        }

        return Ok(new { ok = true, enabled = body.Enabled });
    }

    /// <summary>
    /// Lists the current user's granted items.
    /// </summary>
    /// <returns>The granted items.</returns>
    [HttpGet("Me/Grants")]
    [Authorize]
    public async Task<ActionResult<IEnumerable<ItemDto>>> GetGrants()
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        return Ok(_restrictionManager.GetGrantedItems(userId).Select(ToDto).ToList());
    }

    /// <summary>
    /// Grants an existing library item to the current user.
    /// </summary>
    /// <param name="body">The request body.</param>
    /// <returns>No content.</returns>
    [HttpPost("Me/Grants")]
    [Authorize]
    public async Task<ActionResult> AddGrant([FromBody] GrantItemDto body)
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(body.ItemId, out var itemId) || _libraryManager.GetItemById(itemId) is null)
        {
            return BadRequest("Unknown item id.");
        }

        try
        {
            await _restrictionManager.AddManualGrantAsync(userId, itemId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add grant {ItemId} for user {UserId}", itemId, userId);
            return StatusCode(500, new { error = ex.Message });
        }

        return Ok(new { ok = true });
    }

    /// <summary>
    /// Revokes a granted item from the current user.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Me/Grants/{itemId}")]
    [Authorize]
    public async Task<ActionResult> RemoveGrant([FromRoute] string itemId)
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(itemId, out var parsed))
        {
            return BadRequest("Invalid item id.");
        }

        try
        {
            await _restrictionManager.RemoveGrantAsync(userId, parsed, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove grant {ItemId} for user {UserId}", parsed, userId);
            return StatusCode(500, new { error = ex.Message });
        }

        return Ok(new { ok = true });
    }

    /// <summary>
    /// Searches the whole library for movies/series to grant (unfiltered by the caller's whitelist).
    /// </summary>
    /// <param name="query">The search term.</param>
    /// <returns>Matching items.</returns>
    [HttpGet("Search")]
    [Authorize]
    public async Task<ActionResult<IEnumerable<ItemDto>>> Search([FromQuery] string? query)
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = _grantableKinds,
            Recursive = true,
            IsVirtualItem = false,
            SearchTerm = string.IsNullOrWhiteSpace(query) ? null : query,
            Limit = 50
        });

        return Ok(items.Select(ToDto).ToList());
    }

    /// <summary>
    /// Receives Jellyseerr webhook notifications and grants the requester their media.
    /// </summary>
    /// <param name="payload">The webhook payload.</param>
    /// <returns>An acknowledgement.</returns>
    [HttpPost("Webhook")]
    [AllowAnonymous]
    public async Task<ActionResult> Webhook([FromBody] WebhookPayload payload)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.JellyseerrWebhookSecret))
        {
            _logger.LogWarning("Rejected Jellyseerr webhook: no shared secret configured");
            return StatusCode(403);
        }

        if (!string.Equals(payload.Secret, config.JellyseerrWebhookSecret, StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected Jellyseerr webhook: bad secret");
            return Unauthorized();
        }

        var type = payload.NotificationType?.ToUpperInvariant() ?? string.Empty;
        if (type == "TEST_NOTIFICATION")
        {
            return Ok(new { message = "Private Libraries webhook OK" });
        }

        // Only grant once a request is approved/available.
        if (type != "MEDIA_APPROVED" && type != "MEDIA_AUTO_APPROVED" && type != "MEDIA_AVAILABLE")
        {
            return Ok(new { message = "Ignored notification type " + type });
        }

        var username = payload.Request?.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            _logger.LogWarning("Jellyseerr webhook missing requester username; ignoring");
            return Ok(new { message = "No requester" });
        }

        var user = _userManager.GetUserByName(username);
        if (user is null)
        {
            _logger.LogWarning("Jellyseerr requester '{User}' has no matching Jellyfin user; ignoring", username);
            return Ok(new { message = "No matching Jellyfin user" });
        }

        var tmdb = payload.Media?.TmdbId;
        var tvdb = payload.Media?.TvdbId;
        var granted = false;

        if (!string.IsNullOrWhiteSpace(tmdb))
        {
            await _restrictionManager.AddSeerrGrantAsync(user.Id, "Tmdb", tmdb!, CancellationToken.None).ConfigureAwait(false);
            granted = true;
        }

        if (!string.IsNullOrWhiteSpace(tvdb))
        {
            await _restrictionManager.AddSeerrGrantAsync(user.Id, "Tvdb", tvdb!, CancellationToken.None).ConfigureAwait(false);
            granted = true;
        }

        if (!granted)
        {
            return Ok(new { message = "No usable provider id in payload" });
        }

        _logger.LogInformation("Granted Jellyseerr request to user {User} (tmdb={Tmdb}, tvdb={Tvdb})", username, tmdb, tvdb);
        return Ok(new { message = "Granted" });
    }

    private async Task<Guid> GetUserIdAsync()
    {
        var info = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return info.UserId;
    }

    private static ItemDto ToDto(BaseItem item) => new()
    {
        ItemId = item.Id.ToString("N"),
        Name = item.Name,
        Year = item.ProductionYear,
        Type = item.GetType().Name
    };

    /// <summary>Current user's restriction status.</summary>
    public class MeStatusDto
    {
        /// <summary>Gets or sets a value indicating whether the caller is restricted.</summary>
        public bool RestrictionEnabled { get; set; }

        /// <summary>Gets or sets the number of items granted to the caller.</summary>
        public int GrantedCount { get; set; }
    }

    /// <summary>Body for toggling restriction.</summary>
    public class SetRestrictionDto
    {
        /// <summary>Gets or sets a value indicating whether restriction should be enabled.</summary>
        public bool Enabled { get; set; }
    }

    /// <summary>Body for granting an item.</summary>
    public class GrantItemDto
    {
        /// <summary>Gets or sets the item id to grant.</summary>
        public string ItemId { get; set; } = string.Empty;
    }

    /// <summary>A library item projected for the widget.</summary>
    public class ItemDto
    {
        /// <summary>Gets or sets the item id.</summary>
        public string ItemId { get; set; } = string.Empty;

        /// <summary>Gets or sets the item name.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets the production year.</summary>
        public int? Year { get; set; }

        /// <summary>Gets or sets the item type name.</summary>
        public string? Type { get; set; }
    }

    /// <summary>Jellyseerr webhook payload (fields must match the configured template).</summary>
    public class WebhookPayload
    {
        /// <summary>Gets or sets the notification type.</summary>
        [JsonPropertyName("notification_type")]
        public string? NotificationType { get; set; }

        /// <summary>Gets or sets the shared secret.</summary>
        [JsonPropertyName("secret")]
        public string? Secret { get; set; }

        /// <summary>Gets or sets the media block.</summary>
        [JsonPropertyName("media")]
        public WebhookMedia? Media { get; set; }

        /// <summary>Gets or sets the request block.</summary>
        [JsonPropertyName("request")]
        public WebhookRequest? Request { get; set; }
    }

    /// <summary>Media block of the webhook payload.</summary>
    public class WebhookMedia
    {
        /// <summary>Gets or sets the media type (movie/tv).</summary>
        [JsonPropertyName("media_type")]
        public string? MediaType { get; set; }

        /// <summary>Gets or sets the TMDB id.</summary>
        [JsonPropertyName("tmdbId")]
        public string? TmdbId { get; set; }

        /// <summary>Gets or sets the TVDB id.</summary>
        [JsonPropertyName("tvdbId")]
        public string? TvdbId { get; set; }
    }

    /// <summary>Request block of the webhook payload.</summary>
    public class WebhookRequest
    {
        /// <summary>Gets or sets the requester username.</summary>
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        /// <summary>Gets or sets the requester email.</summary>
        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }
}
