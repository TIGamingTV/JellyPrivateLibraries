using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PrivateLibraries.Configuration;

/// <summary>
/// Plugin configuration. This is the source of truth for who is restricted and
/// which media each user has been granted. The tags written onto library items
/// are always derived from this configuration by <c>RestrictionManager</c>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the base URL of the Jellyseerr instance (e.g. <c>http://jellyseerr:5055</c>).
    /// Required for the backfill sync; the inbound webhook does not use it.
    /// </summary>
    public string JellyseerrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the shared secret that incoming Jellyseerr webhooks must present.
    /// If empty, webhook processing is rejected.
    /// </summary>
    public string JellyseerrWebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyseerr API key used to read existing requests over the REST API.
    /// The webhook only ever reports *new* activity, so this is what lets the plugin
    /// backfill requests made before the plugin was installed. Empty disables the sync.
    /// </summary>
    public string JellyseerrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the periodic Jellyseerr backfill sync runs.
    /// It is additionally a no-op unless <see cref="JellyseerrUrl"/> and
    /// <see cref="JellyseerrApiKey"/> are both set.
    /// </summary>
    public bool JellyseerrSyncEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether requests still awaiting approval are granted
    /// by the sync. Off by default so the sync matches the webhook, which only grants once a
    /// request is approved/available.
    /// </summary>
    public bool JellyseerrSyncIncludePending { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp (round-trip "o" format) of the last completed sync,
    /// or an empty string if it has never run. Shown on the configuration page.
    /// </summary>
    public string JellyseerrLastSyncUtc { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a human-readable summary of the last sync, shown on the configuration page.
    /// </summary>
    public string JellyseerrLastSyncSummary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether every user is restricted automatically
    /// (mandatory mode). When false (default) the plugin is opt-in: a user is only
    /// restricted once they enable it from the widget.
    /// </summary>
    public bool RestrictNewUsersByDefault { get; set; } = false;

    /// <summary>
    /// Gets or sets the configuration schema version, used to run one-time migrations.
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    /// Gets or sets the prefix used to build each user's personal tag (e.g. "jpl").
    /// </summary>
    public string TagPrefix { get; set; } = "jpl";

    /// <summary>
    /// Gets or sets the per-user restriction state.
    /// </summary>
    public List<UserRestrictionEntry> Users { get; set; } = new();

    /// <summary>
    /// Gets or sets the media grants (which user can see which item).
    /// </summary>
    public List<GrantEntry> Grants { get; set; } = new();

    /// <summary>
    /// Gets or sets the items the admin has chosen to hide from everyone except administrators.
    /// These are tagged with the shared hidden tag and blocked in every non-admin user's policy.
    /// </summary>
    public List<HiddenItemEntry> HiddenItems { get; set; } = new();
}

/// <summary>
/// A single admin-hidden item. The item is hidden from every non-administrator
/// regardless of their own restriction state.
/// </summary>
public class HiddenItemEntry
{
    /// <summary>
    /// Gets or sets the Jellyfin item id to hide.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets a cached display name for the item (shown on the config page).
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Per-user restriction state.
/// </summary>
public class UserRestrictionEntry
{
    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the personal tag assigned to this user's granted items.
    /// </summary>
    public string PersonalTag { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the restriction is currently active
    /// for this user. When false the user sees the whole library.
    /// </summary>
    public bool RestrictionEnabled { get; set; } = true;
}

/// <summary>
/// A single media grant. A grant may reference a concrete Jellyfin item (manual
/// selection) and/or an external provider id (Jellyseerr request, resolved once
/// the item is imported).
/// </summary>
public class GrantEntry
{
    /// <summary>
    /// Gets or sets the Jellyfin user id the grant belongs to.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the resolved Jellyfin item id, if known (empty when pending).
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the external provider name (Tmdb, Tvdb, Imdb) for Seerr grants.
    /// This is the Jellyseerr request's *identity*: it is what the webhook dedupes on and
    /// what a still-pending grant is resolved by. Only Seerr grants ever set it — a manual
    /// grant that populated this would be indistinguishable from a Seerr request.
    /// </summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the external provider id value for Seerr grants. See
    /// <see cref="ProviderName"/> for why manual grants must not set this.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider name used purely to *re-find* this grant's item if its
    /// <see cref="ItemId"/> goes stale (the file was replaced by an upgraded copy and
    /// Jellyfin re-imported it under a new id). Deliberately separate from
    /// <see cref="ProviderName"/> so recording it on a manual grant cannot affect
    /// Jellyseerr dedup or pending-request resolution.
    /// </summary>
    public string MatchProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider id used purely to re-find this grant's item. See
    /// <see cref="MatchProviderName"/>.
    /// </summary>
    public string MatchProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where the grant came from (Manual or Seerr).
    /// </summary>
    public string Source { get; set; } = "Manual";
}
