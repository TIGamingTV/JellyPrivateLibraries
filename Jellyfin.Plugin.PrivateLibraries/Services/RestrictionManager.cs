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
        return TagPrefix + ":" + userId.ToString("N", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the shared "hidden" tag applied to admin-hidden items. Every non-admin
    /// user blocks this tag, so those items are visible to administrators only.
    /// </summary>
    /// <returns>The hidden tag.</returns>
    public static string BuildHiddenTag()
    {
        return TagPrefix + ":hidden";
    }

    private static string TagPrefix =>
        string.IsNullOrWhiteSpace(Config.TagPrefix) ? "jpl" : Config.TagPrefix.Trim();

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
    /// Ensures every non-admin user blocks the shared hidden tag (so admin-hidden items
    /// are invisible to them) while administrators never block it. When no items are
    /// hidden the tag is removed from everyone's policy as cleanup.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>A task.</returns>
    public async Task SyncUserBlockedTagsAsync(Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return;
        }

        var policy = _userManager.GetUserDto(user).Policy;
        if (policy is null)
        {
            return;
        }

        var hiddenTag = BuildHiddenTag();
        var shouldBlock = Config.HiddenItems.Count > 0 && !policy.IsAdministrator;

        var blocked = (policy.BlockedTags ?? Array.Empty<string>())
            .Where(t => !string.Equals(t, hiddenTag, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (shouldBlock)
        {
            blocked.Add(hiddenTag);
        }

        var newBlocked = blocked.ToArray();
        var current = policy.BlockedTags ?? Array.Empty<string>();
        if (current.Length == newBlocked.Length && !current.Except(newBlocked, StringComparer.OrdinalIgnoreCase).Any())
        {
            return; // No change.
        }

        policy.BlockedTags = newBlocked;
        await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
        _logger.LogInformation(
            "Updated BlockedTags for user {UserId}: hidden tag {State}",
            userId,
            shouldBlock ? "BLOCKED" : "CLEARED");
    }

    /// <summary>
    /// Applies the current hidden-item blocked-tag state to every Jellyfin user.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task SyncAllBlockedTagsAsync()
    {
        foreach (var user in _userManager.GetUsers())
        {
            await SyncUserBlockedTagsAsync(user.Id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Marks a library item as hidden from everyone except administrators, tagging it and
    /// updating every non-admin user's policy immediately.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the item was newly hidden.</returns>
    public async Task<bool> AddHiddenItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return false;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Config.HiddenItems.Any(h => h.ItemId == itemId))
            {
                return false;
            }

            Config.HiddenItems.Add(new HiddenItemEntry { ItemId = itemId, Name = item.Name ?? string.Empty });
            Plugin.Instance!.SaveConfiguration();
        }
        finally
        {
            _lock.Release();
        }

        await AddTagToItemAsync(item, BuildHiddenTag(), cancellationToken).ConfigureAwait(false);
        await SyncAllBlockedTagsAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Un-hides a previously hidden item, removing its tag and refreshing user policies.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RemoveHiddenItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Config.HiddenItems.RemoveAll(h => h.ItemId == itemId);
            Plugin.Instance!.SaveConfiguration();
        }
        finally
        {
            _lock.Release();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is not null)
        {
            await RemoveTagFromItemAsync(item, BuildHiddenTag(), cancellationToken).ConfigureAwait(false);
        }

        await SyncAllBlockedTagsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the library items that are currently hidden (skipping any that no longer exist).
    /// </summary>
    /// <returns>The hidden items.</returns>
    public IReadOnlyList<BaseItem> GetHiddenItems()
    {
        var items = new List<BaseItem>();
        foreach (var hidden in Config.HiddenItems)
        {
            var item = _libraryManager.GetItemById(hidden.ItemId);
            if (item is not null && items.All(i => i.Id != item.Id))
            {
                items.Add(item);
            }
        }

        return items;
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

        // Snapshot the matching grants under the lock. This handler runs fire-and-forget
        // for every imported item (potentially thousands during a scan), so it can race
        // with reconcile / API handlers mutating Config.Grants; enumerating the live list
        // here without synchronization can throw "Collection was modified".
        List<GrantEntry> matching;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            matching = Config.Grants.Where(g =>
                g.ItemId == Guid.Empty
                && !string.IsNullOrEmpty(g.ProviderName)
                && item.ProviderIds.TryGetValue(g.ProviderName, out var value)
                && string.Equals(value, g.ProviderId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        finally
        {
            _lock.Release();
        }

        if (matching.Count == 0)
        {
            return;
        }

        foreach (var grant in matching)
        {
            var tag = Config.Users.FirstOrDefault(u => u.UserId == grant.UserId)?.PersonalTag
                      ?? BuildPersonalTag(grant.UserId);
            await AddTagToItemAsync(item, tag, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Applied pending grant for user {UserId} to newly added item {Item}", grant.UserId, item.Name);
        }

        // Cache the resolved item id and persist under the lock so we don't collide with a
        // concurrent SaveConfiguration from reconcile.
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var grant in matching)
            {
                grant.ItemId = item.Id;
            }

            Plugin.Instance!.SaveConfiguration();
        }
        finally
        {
            _lock.Release();
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
        var users = _userManager.GetUsers().ToList();

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // One-time migration to the opt-in model: clear every existing restriction so
            // no user is restricted unless they turn it on themselves.
            if (Config.SchemaVersion < 1)
            {
                foreach (var entry in Config.Users)
                {
                    entry.RestrictionEnabled = false;
                }

                Config.SchemaVersion = 1;
                Plugin.Instance!.SaveConfiguration();
            }

            // Mandatory mode only: auto-enroll every user so they are restricted by default.
            if (Config.RestrictNewUsersByDefault)
            {
                foreach (var user in users)
                {
                    EnsureUserEntryLocked(user.Id);
                }

                Plugin.Instance!.SaveConfiguration();
            }
        }
        finally
        {
            _lock.Release();
        }

        // Only touch the policies of users the plugin actually manages (opted in / has an
        // entry). Users who never engaged the plugin are left completely untouched.
        var managedUserIds = Config.Users.Select(u => u.UserId).ToList();
        foreach (var userId in managedUserIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SyncUserPolicyAsync(userId).ConfigureAwait(false);
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

        // Persist the resolved item ids cached by ApplyGrantAsync under the lock so this
        // write cannot race with a concurrent SaveConfiguration from the item-added handler.
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Plugin.Instance!.SaveConfiguration();
        }
        finally
        {
            _lock.Release();
        }

        // 3. (Re)apply the hidden tag to every admin-hidden item and push the
        //    blocked-tag state to all users (hiding applies to everyone, not just
        //    the plugin-managed/opted-in users).
        var hiddenTag = BuildHiddenTag();
        foreach (var hidden in Config.HiddenItems.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = _libraryManager.GetItemById(hidden.ItemId);
            if (item is not null)
            {
                await AddTagToItemAsync(item, hiddenTag, cancellationToken).ConfigureAwait(false);
            }
        }

        await SyncAllBlockedTagsAsync().ConfigureAwait(false);
        progress?.Report(90);

        // 4. Strip personal/hidden tags from items that no longer have a backing grant.
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

        // The shared hidden tag also starts with the prefix; keep it on every
        // still-hidden item so it is not treated as an orphan and stripped.
        var hiddenTag = BuildHiddenTag();
        foreach (var hidden in Config.HiddenItems)
        {
            expected.Add(hidden.ItemId.ToString("N", CultureInfo.InvariantCulture) + "|" + hiddenTag);
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
        if (tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return;
        }

        item.Tags = tags.ToArray();

        // Once no plugin-managed tags remain on the item, release the Tags-field lock we
        // took when granting/hiding. Otherwise the lock would persist forever after the
        // last grant is revoked (or the item is un-hidden), permanently preventing
        // metadata refreshes from ever managing this item's tags again.
        var prefix = TagPrefix + ":";
        if (!tags.Any(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            UnlockTagsField(item);
        }

        await _libraryManager.UpdateItemAsync(item, item.GetParent() ?? item, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
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

    private static void UnlockTagsField(BaseItem item)
    {
        var locked = item.LockedFields;
        if (locked is null || locked.Length == 0 || !locked.Contains(MetadataField.Tags))
        {
            return;
        }

        item.LockedFields = locked.Where(f => f != MetadataField.Tags).ToArray();
    }
}
