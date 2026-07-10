# Progress log

A running history of changes to JellyPrivateLibraries.

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

**Notes / not yet done**

- Not compiled in this environment (the .NET SDK host was blocked by egress
  policy); CI (`build.yml`) is the first compile checkpoint.
- No automated tests yet.
- Series grants tag the series item; episode visibility relies on Jellyfin's
  inherited-tag parental filtering.
