# Progress log

A running history of changes to JellyPrivateLibraries.

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
