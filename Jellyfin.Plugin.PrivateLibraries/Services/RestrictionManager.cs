using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PrivateLibraries.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PrivateLibraries.Services;

/// <summary>
/// Core service that keeps Jellyfin's per-user allowed-tags whitelist and the
/// item-level tags in sync with the plugin configuration (the source of truth).
/// </summary>
public class RestrictionManager
{
    private static readonly BaseItemKind[] _grantableKinds = { BaseItemKind.Movie, BaseItemKind.Series };
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<RestrictionManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="RestrictionManager"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="logger">The logger.</param>
    public RestrictionManager(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<RestrictionManager> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Builds the personal tag for a user id using the configured prefix.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The personal tag.</returns>
    public static string BuildPersonalTag(Guid userId)
    {
        var prefix = string.IsNullOrWhiteSpace(Config.TagPrefix) ? "jpl" : Config.TagPrefix.Trim();
        return prefix + ":" + userId.ToString("N", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Ensures a configuration entry exists for the given user, creating one if needed.
    /// Caller is responsible for holding the lock and saving.
    /// </summary>
    private static UserRestrictionEntry EnsureUserEntryLocked(Guid userId)
    {
        var entry = Config.Users.FirstOrDefault(u => u.UserId == userId);
        if (entry is null)
        {
            entry = new UserRestrictionEntry
            {
                UserId = userId,
                PersonalTag = BuildPersonalTag(userId),
                RestrictionEnabled = Config.RestrictNewUsersByDefault
            };
            Config.Users.Add(entry);
        }
        else if (string.IsNullOrEmpty(entry.PersonalTag))
        {
            entry.PersonalTag = BuildPersonalTag(userId);
        }

        return entry;
    }

    /// <summary>
    /// Gets whether the given user currently has restriction enabled.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>True if restricted.</returns>
    public bool IsRestrictionEnabled(Guid userId)
    {
        var entry = Config.Users.FirstOrDefault(u => u.UserId == userId);
        return entry?.RestrictionEnabled ?? Config.RestrictNewUsersByDefault;
    }

    /// <summary>
    /// Enables or disables the restriction for a single user and updates their policy live.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="enabled">Whether to restrict.</param>
    /// <returns>A task.</returns>
    public async Task SetRestrictionEnabledAsync(Guid userId, bool enabled)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = EnsureUserEntryLocked(userId);
            entry.RestrictionEnabled = enabled;
            Plugin.Instance!.SaveConfiguration();
        }
        finally
        {
            _lock.Release();
        }

        await SyncUserPolicyAsync(userId).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the user's restriction state to their Jellyfin policy's AllowedTags whitelist.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>A task.</returns>
    public async Task SyncUserPolicyAsync(Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return;
        }

        var entry = Config.Users.FirstOrDefault(u => u.UserId == userId);
        var tag = entry?.PersonalTag ?? BuildPersonalTag(userId);
        var enabled = entry?.RestrictionEnabled ?? Config.RestrictNewUsersByDefault;

        var policy = _userManager.GetUserDto(user).Policy;
        if (policy is null)
        {
            return;
        }

        var allowed = (policy.AllowedTags ?? Array.Empty<string>())
            .Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (enabled)
        {
            allowed.Add(tag);
        }

        var newAllowed = allowed.ToArray();
        var current = policy.AllowedTags ?? Array.Empty<string>();
        if (current.Length == newAllowed.Length && !current.Except(newAllowed, StringComparer.OrdinalIgnoreCase).Any())
        {
            return; // No change.
        }

        policy.AllowedTags = newAllowed;
        await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
        _logger.LogInformation(
            "Updated AllowedTags for user {UserId}: restriction {State}",
            userId,
            enabled ? "ENABLED" : "DISABLED");
    }

    /// <summary>
    /// Adds a manual grant (existing library item) for a user and tags it immediately.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the grant was added.</returns>
    public async Task<bool> AddManualGrantAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        GrantEntry grant;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUserEntryLocked(userId);
            var existing = Config.Grants.FirstOrDefault(g => g.UserId == userId && g.ItemId == itemId);
            if (existing is not null)
            {
                return false;
            }

            grant = new GrantEntry { UserId = userId, ItemId = itemId, Source = "Manual" };
            Config.Grants.Add(grant);
            Plugin.Instance!.SaveConfiguration();
        }
        finally
        {
            _lock.Release();
        }

        await ApplyGrantAsync(grant, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Adds (or resolves) a Seerr grant identified by an external provider id.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="providerName">The provider name (Tmdb/Tvdb/Imdb).</param>
    /// <param name="providerId">The provider id value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task AddSeerrGrantAsync(Guid userId, string providerName, string providerId, CancellationToken cancellationToken)
    {
        GrantEntry grant;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUserEntryLocked(userId);
            var existing = Config.Grants.FirstOrDefault(g =>
                g.UserId == userId
                && string.Equals(g.ProviderName, providerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(g.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return;
            }

            grant = new GrantEntry
            {
                UserId = userId,
                ProviderName = providerName,
                ProviderId = providerId,
                Source = "Seerr"
            };
            Config.Grants.Add(grant);
            Plugin.Instance!.SaveConfiguration();
        }
        finally
        {
            _lock.Release();
        }

        // Item may not be imported yet; ApplyGrantAsync is a no-op until it exists.
        await ApplyGrantAsync(grant, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a manual grant for an item and untags it if no other grant needs it.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RemoveGrantAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Config.Grants.RemoveAll(g => g.UserId == userId && g.ItemId == itemId);
            Plugin.Instance!.SaveConfiguration();
        }
        finally
        {
            _lock.Release();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is not null)
        {
            var tag = Config.Users.FirstOrDefault(u => u.UserId == userId)?.PersonalTag ?? BuildPersonalTag(userId);
            await RemoveTagFromItemAsync(item, tag, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lists the item ids a user has been granted (with resolved items where possible).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The granted items.</returns>
    public IReadOnlyList<BaseItem> GetGrantedItems(Guid userId)
    {
        var items = new List<BaseItem>();
        foreach (var grant in Config.Grants.Where(g => g.UserId == userId))
        {
            foreach (var item in ResolveGrantItems(grant))
            {
                if (items.All(i => i.Id != item.Id))
                {
                    items.Add(item);
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Resolves the concrete library item(s) a grant refers to.
    /// </summary>
    private IReadOnlyList<BaseItem> ResolveGrantItems(GrantEntry grant)
    {
        if (grant.ItemId != Guid.Empty)
        {
            var byId = _libraryManager.GetItemById(grant.ItemId);
            return byId is null ? Array.Empty<BaseItem>() : new[] { byId };
        }

        if (!string.IsNullOrEmpty(grant.ProviderName) && !string.IsNullOrEmpty(grant.ProviderId))
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = _grantableKinds,
                Recursive = true,
                IsVirtualItem = false,
                HasAnyProviderId = new Dictionary<string, string>
                {
                    [grant.ProviderName] = grant.ProviderId
                }
            };
            return _libraryManager.GetItemList(query);
        }

        return Array.Empty<BaseItem>();
    }

    /// <summary>
    /// Applies the personal tag for a grant onto its resolved item(s).
    /// </summary>
    /// <param name="grant">The grant.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if at least one item was tagged.</returns>
    public async Task<bool> ApplyGrantAsync(GrantEntry grant, CancellationToken cancellationToken)
    {
        var tag = Config.Users.FirstOrDefault(u => u.UserId == grant.UserId)?.PersonalTag
                  ?? BuildPersonalTag(grant.UserId);

        var applied = false;
        foreach (var item in ResolveGrantItems(grant))
        {
            if (await AddTagToItemAsync(item, tag, cancellationToken).ConfigureAwait(false))
            {
                applied = true;
            }

            // Cache the resolved item id back on Seerr grants for faster future reconciles.
            if (grant.ItemId == Guid.Empty)
            {
                grant.ItemId = item.Id;
            }
        }

        return applied;
    }

    /// <summary>
    /// Handles a freshly-added library item by applying any pending grants that match it.
    /// </summary>
    /// <param name="item">The newly added item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task OnItemAddedAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item?.ProviderIds is null || item.ProviderIds.Count == 0)
        {
            return;
        }

        var matching = Config.Grants.Where(g =>
            g.ItemId == Guid.Empty
            && !string.IsNullOrEmpty(g.ProviderName)
            && item.ProviderIds.TryGetValue(g.ProviderName, out var value)
            && string.Equals(value, g.ProviderId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var grant in matching)
        {
            var tag = Config.Users.FirstOrDefault(u => u.UserId == grant.UserId)?.PersonalTag
                      ?? BuildPersonalTag(grant.UserId);
            await AddTagToItemAsync(item, tag, cancellationToken).ConfigureAwait(false);
            grant.ItemId = item.Id;
            _logger.LogInformation("Applied pending grant for user {UserId} to newly added item {Item}", grant.UserId, item.Name);
        }

        if (matching.Count > 0)
        {
            Plugin.Instance!.SaveConfiguration();
        }
    }

    /// <summary>
    /// Full reconcile: ensure every user has an entry and correct policy, and every
    /// grant is applied to its item(s); also strip orphaned personal tags.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ReconcileAllAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // 1. Ensure entries + policies for all users.
        var users = _userManager.GetUsers().ToList();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var user in users)
            {
                EnsureUserEntryLocked(user.Id);
            }

            Plugin.Instance!.SaveConfiguration();
        }
        finally
        {
            _lock.Release();
        }

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SyncUserPolicyAsync(user.Id).ConfigureAwait(false);
        }

        progress?.Report(25);

        // 2. Apply all grants (also caches resolved item ids).
        var grants = Config.Grants.ToList();
        for (var i = 0; i < grants.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyGrantAsync(grants[i], cancellationToken).ConfigureAwait(false);
            progress?.Report(25 + (60.0 * (i + 1) / Math.Max(1, grants.Count)));
        }

        Plugin.Instance!.SaveConfiguration();

        // 3. Strip personal tags from items that no longer have a backing grant.
        await CleanOrphanTagsAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
    }

    /// <summary>
    /// Removes personal tags from items that are tagged but no longer granted.
    /// </summary>
    private async Task CleanOrphanTagsAsync(CancellationToken cancellationToken)
    {
        var prefix = (string.IsNullOrWhiteSpace(Config.TagPrefix) ? "jpl" : Config.TagPrefix.Trim()) + ":";

        // Build the set of (itemId, tag) pairs that SHOULD exist.
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in Config.Grants)
        {
            var tag = Config.Users.FirstOrDefault(u => u.UserId == grant.UserId)?.PersonalTag
                      ?? BuildPersonalTag(grant.UserId);
            foreach (var item in ResolveGrantItems(grant))
            {
                expected.Add(item.Id.ToString("N", CultureInfo.InvariantCulture) + "|" + tag);
            }
        }

        var tagged = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = _grantableKinds,
            Recursive = true,
            IsVirtualItem = false
        }).Where(i => i.Tags is not null && i.Tags.Any(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

        foreach (var item in tagged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var orphanTags = item.Tags!
                .Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(t => !expected.Contains(item.Id.ToString("N", CultureInfo.InvariantCulture) + "|" + t))
                .ToList();

            foreach (var tag in orphanTags)
            {
                await RemoveTagFromItemAsync(item, tag, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Adds a tag to an item (and locks the Tags field so refreshes cannot wipe it).
    /// </summary>
    private async Task<bool> AddTagToItemAsync(BaseItem item, string tag, CancellationToken cancellationToken)
    {
        var tags = (item.Tags ?? Array.Empty<string>()).ToList();
        if (tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        tags.Add(tag);
        item.Tags = tags.ToArray();
        LockTagsField(item);
        await _libraryManager.UpdateItemAsync(item, item.GetParent() ?? item, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Removes a tag from an item.
    /// </summary>
    private async Task RemoveTagFromItemAsync(BaseItem item, string tag, CancellationToken cancellationToken)
    {
        var tags = (item.Tags ?? Array.Empty<string>()).ToList();
        if (tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            item.Tags = tags.ToArray();
            await _libraryManager.UpdateItemAsync(item, item.GetParent() ?? item, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void LockTagsField(BaseItem item)
    {
        var locked = (item.LockedFields ?? Array.Empty<MetadataField>()).ToList();
        if (!locked.Contains(MetadataField.Tags))
        {
            locked.Add(MetadataField.Tags);
            item.LockedFields = locked.ToArray();
        }
    }
}
