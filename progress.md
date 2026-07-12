# Progress log

A running history of changes to JellyPrivateLibraries.

## 2026-07-12 — Docs sync + 1.0.0.6 release note

- Documentation refresh (no behaviour change):
  - `CLAUDE.md` "Build & verify" corrected — users start **unrestricted**, not
    restricted (restriction has been opt-in since v1.0.0.4); verification now says to
    toggle restriction *on* and confirm the library narrows.
  - `CLAUDE.md` "Key files" now lists `Services/ItemAddedListener.cs` (the
    `IHostedService` wiring `ILibraryManager.ItemAdded` → `OnItemAddedAsync`) and
    `Configuration/PluginConfiguration.cs` (the source of truth).
  - Added a `CLAUDE.md` "Releasing" section describing the tag-driven
    `release.yml` flow and the manifest-vs-source version drift.
- Release note: the 2026-07-11 audit fixes were published as **1.0.0.6** via a
  `v1.0.0.6` tag; `release.yml` auto-prepended the manifest entry and committed it to
  `main` (`f143421`). The audit itself was source-version-neutral, so `csproj`/`build.yaml`
  still read **1.0.0.5** while `manifest.json` leads at **1.0.0.6** — a known drift to
  reconcile on the next intentional version bump.

## 2026-07-11 — Repository audit: reliability & security hardening

Findings-driven, low-risk fixes (no version bump; behaviour is otherwise unchanged):

- **Concurrency crash (reliability).** `RestrictionManager.OnItemAddedAsync` enumerated the
  live `Config.Grants` list with no synchronization while it runs fire-and-forget for every
  imported item. During a library scan this races reconcile / API mutations of the same list
  and can throw `InvalidOperationException` ("Collection was modified"), aborting tag
  application. It now snapshots the matching grants under the existing `_lock`, applies tags
  outside the lock, then persists the cached item ids under the lock. Reconcile's post-grant
  `SaveConfiguration` was likewise wrapped in the lock so the two writers can't collide.
- **Permanent metadata lock leak (correctness).** Granting/hiding locks an item's `Tags`
  field, but `RemoveTagFromItemAsync` never released it, so once the last grant was revoked
  (or an item un-hidden) the `Tags` field stayed locked forever, blocking future metadata
  refreshes from ever managing that item's tags again. It now unlocks `MetadataField.Tags`
  only when no plugin-prefixed tags remain on the item (the shared hidden tag counts, so a
  still-hidden item keeps its lock).
- **Timing-safe webhook secret (security).** The Jellyseerr webhook compared the shared
  secret with an ordinary ordinal string comparison, which short-circuits and is
  timing-observable. Replaced with `CryptographicOperations.FixedTimeEquals` over UTF-8
  bytes.

## 2026-07-11 — v1.0.0.5: admin-hidden media

- New feature: the admin can pick movies/series on the plugin config page to hide
  from **everyone except administrators**, independent of each user's own restriction.
- Mechanism: a shared hidden tag `<prefix>:hidden` is applied (and locked) to each
  hidden item; every non-admin user's `UserPolicy.BlockedTags` gets that tag, while
  admins never do. `BlockedTags` takes precedence over `AllowedTags`, so a hidden item
  stays hidden even if a restricted user was also granted it. When the last hidden item
  is removed the tag is cleared from every policy (self-cleaning).
- `PluginConfiguration`: added `HiddenItems` (`List<HiddenItemEntry>` of ItemId + cached Name).
- `RestrictionManager`: `BuildHiddenTag`, `AddHiddenItemAsync`/`RemoveHiddenItemAsync`,
  `GetHiddenItems`, `SyncUserBlockedTagsAsync`/`SyncAllBlockedTagsAsync`. Reconcile now
  re-applies the hidden tag to all hidden items and syncs BlockedTags for **all** users
  (hiding is server-wide, not opt-in scoped); orphan cleanup preserves the hidden tag.
- `RestrictionController`: admin-only (`Policies.RequiresElevation`) endpoints
  `GET/POST Hidden`, `DELETE Hidden/{itemId}`, `GET Hidden/Search`.
- Config page: new "Hidden media" section (search → Hide, list → Unhide) that calls the
  admin endpoints directly via `ApiClient.ajax` and takes effect immediately.
- Bumped `1.0.0.4` → `1.0.0.5`.

## 2026-07-10 — v1.0.0.4: make restriction opt-in (not mandatory)

- The plugin restricted every user by default, so libraries started hidden and the
  widget toggle opened as "restricted" — confusing and mandatory. Switched to opt-in.
- `PluginConfiguration.RestrictNewUsersByDefault` default → `false`; added `SchemaVersion`.
- `RestrictionManager.ReconcileAllAsync`: one-time migration (SchemaVersion < 1) resets all
  existing entries to unrestricted; auto-enroll-all now only runs in mandatory mode; policy
  sync only touches users the plugin manages (opted in), leaving everyone else untouched.
- Config page: the checkbox is now "Mandatory mode" (off by default), intro text clarified.
- Widget copy clarifies off = full library, on = restricted.
- Verified working per user report: switch, button, manual add. Seerr still untested.
- Bumped `1.0.0.3` → `1.0.0.4`.

## 2026-07-10 — v1.0.0.3: fix authorization policy (500s)

- Every authenticated endpoint returned HTTP 500:
  `The AuthorizationPolicy named: 'DefaultAuthorization' was not found`. That policy
  was removed in Jellyfin 10.11 (policies moved to `MediaBrowser.Common/Api/Policies.cs`).
  Replaced `[Authorize(Policy = "DefaultAuthorization")]` with bare `[Authorize]` on all
  widget endpoints (matches how Jellyfin 10.11's own controllers authorize).
- This was the real cause of the widget doing nothing; the v1.0.0.2 `ApiClient.ajax`
  switch and error surfacing stay (they made this error visible).
- Bumped `1.0.0.2` → `1.0.0.3`.

## 2026-07-10 — v1.0.0.2: fix widget API auth + UX

- Widget actions (restriction toggle, add/remove title, search) all silently
  no-op'd: `Web/private-libraries.js` used raw `fetch` with a legacy `X-Emby-Token`
  header, which Jellyfin 10.11 rejects, and swallowed all errors. Rewrote the API
  helpers to use `ApiClient.ajax(...)` (correct `Authorization: MediaBrowser` header),
  surface errors inline + in the console, and reload the page on dialog close when a
  change was made so toggling/granting is reflected in the library.
- `Api/RestrictionController.cs`: write endpoints now return `Ok(...)` JSON instead of
  204 (so `ApiClient.ajax` doesn't choke) and wrap operations in try/catch with logging.
- Bumped version `1.0.0.1` → `1.0.0.2`.
- Existing-media handling: manual widget only (no Jellyseerr API backfill).

## 2026-07-10 — v1.0.0.1: fix widget script path

- The injected `<script>` used a relative `src="PrivateLibraries/ClientScript"`, which
  resolved to `<base>/web/PrivateLibraries/ClientScript` (404, empty MIME → "Refused to
  execute") because the controller lives at `<base>/PrivateLibraries/ClientScript`.
  Changed the src to `../PrivateLibraries/ClientScript` in `Services/ScriptInjector.cs`.
- Bumped version `1.0.0.0` → `1.0.0.1` (csproj + `build.yaml`).

## 2026-07-10 — Initial implementation

Built the plugin from scratch (repo previously held only README + LICENSE).

**Added**

- Plugin scaffold targeting Jellyfin `10.11.*` / net9.0: `Plugin.cs`,
  `PluginConfiguration.cs`, `PluginServiceRegistrator.cs`, `.csproj`,
  `build.yaml`, `manifest.json`, `.github/workflows/build.yml`, `.gitignore`.
- `Services/RestrictionManager.cs` — core service: per-user personal tags,
  `AllowedTags` policy sync (read via `GetUserDto().Policy`, write via
  `UpdatePolicyAsync`), grant add/remove, provider-id → item resolution
  (`HasAnyProviderId`), tag apply/remove with `MetadataField.Tags` locking,
  full reconcile, and orphan-tag cleanup.
- `ScheduledTasks/ReconcileTask.cs` — startup + 30-min interval reconcile.
- `Services/ItemAddedListener.cs` — real-time tagging of newly imported media
  that matches a pending Jellyseerr grant.
- `Services/ScriptInjector.cs` — injects the widget loader into `index.html`
  (Intro-Skipper pattern), idempotent, tolerant of read-only web roots.
- `Api/RestrictionController.cs` — widget REST API (`Me`, `Me/Restriction`,
  `Me/Grants`, `Search`) scoped to the caller via `IAuthorizationContext`, plus
  the `/PrivateLibraries/Webhook` Jellyseerr receiver (secret-validated).
- `Web/private-libraries.js` — home-screen button + dialog: restriction toggle,
  library search to add titles, and current-grants list to remove.
- `Configuration/configPage.html` — admin page for tag prefix, default-restrict
  toggle, Jellyseerr URL/secret, and webhook setup instructions.
- Rewrote `README.md`; added `CLAUDE.md`.

**Build fixes (same day)**

- Retargeted `net8.0` → `net9.0` (Jellyfin.Controller 10.11 supports net9.0 only;
  net8.0 restore failed with NU1202).
- Removed a stale `using Jellyfin.Data.Entities;` — the `User` entity moved out of
  that namespace in 10.11 (CS0234).
- **CI is green:** the plugin compiles against Jellyfin 10.11 / .NET 9 and the
  `Jellyfin.Plugin.PrivateLibraries.dll` artifact is produced.

**Notes / not yet done**

- Not run inside a live Jellyfin server yet (no runtime env here); verified by a
  clean CI compile. End-to-end verification steps are in the README.
- No automated tests yet.
- Series grants tag the series item; episode visibility relies on Jellyfin's
  inherited-tag parental filtering.
