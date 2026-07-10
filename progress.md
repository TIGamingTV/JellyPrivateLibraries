# Progress log

A running history of changes to JellyPrivateLibraries.

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
