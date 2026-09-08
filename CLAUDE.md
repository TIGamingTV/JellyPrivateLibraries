# CLAUDE.md

Guidance for working in this repository.

## What this is

A **Jellyfin server plugin** (C#, .NET 10, targets Jellyfin 12.0.x) that restricts
each user to only the media they requested via Jellyseerr or added from a
home-screen widget. It is built on Jellyfin's native per-user **allowed-tags
whitelist** (`UserPolicy.AllowedTags`).

Jellyfin 10.11.x (net9.0) support was **removed in v1.10.0.0** — see "Dropped
Jellyfin 10.11 support" below. Do not reintroduce dual-targeting without a strong
reason; it doubled the SDK/CI/manifest/release surface for identical source.

## Core mechanism (read before changing logic)

- Each user has a **personal tag** `"<prefix>:<userId:N>"` (prefix default `jpl`).
- A user's `UserPolicy.AllowedTags` contains their personal tag **iff** their
  restriction is enabled → they only see items tagged with it.
- Granting a title = add the personal tag to the item's `Tags` and lock the
  `Tags` metadata field so refreshes don't wipe it.
- The **plugin configuration is the source of truth** (`PluginConfiguration.Users`
  and `.Grants`). Item tags and user policies are *derived* and re-applied by the
  reconcile task; never treat item tags as authoritative.
- **Admin-hidden media** is the inverse: items in `PluginConfiguration.HiddenItems`
  are tagged with a shared hidden tag `"<prefix>:hidden"` and that tag is added to
  every non-admin user's `UserPolicy.BlockedTags` (admins are never blocked).
  `BlockedTags` beats `AllowedTags`, so hidden items stay hidden even for a restricted
  user who was also granted them. This applies to **all** users, not just opted-in ones.

## Key files

- `Plugin.cs` — `BasePlugin<PluginConfiguration>`, `IHasWebPages`. Plugin GUID:
  `a3f1c6d2-9b4e-4c8a-bf2d-7e5a1c9d40e1` (keep in sync with `build.yaml`,
  `manifest.json`, and `configPage.html`).
- `Services/RestrictionManager.cs` — all tag/policy logic. Uses
  `IUserManager.GetUserDto(user).Policy` to read the current policy and
  `UpdatePolicyAsync` to write it; `ILibraryManager` for item lookup/update.
  Item lookup by provider id uses `InternalItemsQuery.HasAnyProviderId`.
- `Api/RestrictionController.cs` — widget REST API (identity resolved via
  `IAuthorizationContext.GetAuthorizationInfo(Request)`) + `/PrivateLibraries/Webhook`.
  Authenticated endpoints use bare `[Authorize]` — the old `DefaultAuthorization`
  policy was removed in 10.11 and naming it throws "policy not found" (500).
- `Services/ItemAddedListener.cs` — `IHostedService` that hooks
  `ILibraryManager.ItemAdded` and calls `RestrictionManager.OnItemAddedAsync`
  fire-and-forget to tag newly imported media matching a pending grant.
- `Services/ScriptInjector.cs` — injects the widget `<script>` tag into `index.html`.
  Prefers registering a transformation with the community "File Transformation" plugin
  (https://github.com/IAmParadox27/jellyfin-plugin-file-transformation, discovered via
  reflection since plugins load into separate assembly contexts) so the file is patched
  in memory per-request, same mechanism other plugins (Intro Skipper, Home Screen
  Sections) use — this doesn't clobber their injections either. Falls back to directly
  read/patch/write of `index.html` (old Intro-Skipper-style pattern, marker-delimited)
  only if that plugin isn't installed. See `Services/FileTransformationPayload.cs` for
  the callback payload type.
- `Web/private-libraries.js` — injected widget (vanilla JS, no build step).
- `ScheduledTasks/ReconcileTask.cs` — startup + 30-min interval reconcile.
- `Configuration/PluginConfiguration.cs` — the source of truth (`Users`,
  `Grants`, `HiddenItems`, `TagPrefix`, `RestrictNewUsersByDefault`,
  `SchemaVersion`) + config page (`configPage.html`).

## Supported server version

Single TFM, single Jellyfin line:

| TFM | Jellyfin | `Jellyfin.Controller` | Manifest | Release zip |
|---|---|---|---|---|
| `net10.0` | 12.0.x | `12.0.0` (pinned) | `manifest.json` | `private-libraries_<v>_jf12.zip` |

- Jellyfin **12.0 is 10.12 renamed** (the leading `10.` was dropped); the plugin API
  is unchanged from 10.11.
- The reference is **pinned to an exact version on purpose**: a stray `12.0.0-rcrc3`
  package existed on nuget.org that sorted *above* the real prereleases under SemVer
  ordering, so a floating `12.0.*` selector risked silently resolving to it. Revisit
  the pin as newer 12.0.x releases ship.
- `build.yaml` is the descriptor for the artifact. Nothing in CI consumes it; it is
  metadata and must be kept in step with `manifest.json`.

### Dropped Jellyfin 10.11 support (net9.0)

Removed in `v1.10.0.0`. The plugin previously multi-targeted `net9.0` (Jellyfin
10.11.x) and `net10.0` (Jellyfin 12.0.x) from **identical source** — there were never
any `#if`/conditional-compilation differences, only a different `Jellyfin.Controller`
`PackageReference` per TFM. Dual-targeting was dropped to stop doubling the SDK/CI/
manifest/release surface for a build with no source differences. Versions up to
`v1.9.2.0` still work on Jellyfin 10.11.x; those users can install one of those
releases manually (see README "Installing") but will not get further updates through
the plugin repository, since `manifest.json` now only carries `targetAbi 12.0.0.0`
entries going forward.

If 10.11 support is ever needed again: reintroduce `<TargetFrameworks>net9.0;net10.0
</TargetFrameworks>`, a `Condition="'$(TargetFramework)' == 'net9.0'"` `ItemGroup`
pinning `Jellyfin.Controller` to `10.11.*`, and a second manifest (Jellyfin's
`VersionInfo.targetAbi` is a **minimum** version with no `maxAbi`, so one manifest
can't safely describe both builds — a 12.x server reading a `10.11.0.0` entry would
consider it compatible and install the wrong DLL).

## Jellyfin API notes (targets 12.0)

- `TaskTriggerInfo.Type` is the **enum** `TaskTriggerInfoType` (not a string).
- `IUserManager`: `GetUsers()`, `GetUserById`, `GetUserByName`, `GetUserDto`,
  `UpdatePolicyAsync`. There is **no** `GetUserPolicy` — read via `GetUserDto`.
- `ILibraryManager.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, ct)`.
- `MetadataField.Tags` is the lockable field for tags.

## Build & verify

Requires the .NET 10 SDK.

```bash
dotnet build Jellyfin.Plugin.PrivateLibraries/Jellyfin.Plugin.PrivateLibraries.csproj -c Release
# -> bin/Release/net10.0/...dll  (Jellyfin 12.0)
```

A clean build is **0 warnings, 0 errors**; treat any new warning as a regression.

There is no unit test project yet. Manual verification: load the DLL into a test
Jellyfin (12.0), create two users, confirm each starts **unrestricted** (restriction
is opt-in since v1.0.0.4 — `RestrictNewUsersByDefault` defaults to `false`), toggle
restriction on from the widget and confirm the library then narrows to granted titles,
grant a title, toggle restriction off/on, and fire a Jellyseerr test/approved webhook.
See `progress.md` for history.

## Releasing

- `.github/workflows/release.yml` runs on a pushed `v*` tag (or manual dispatch):
  it builds the plugin, packages `private-libraries_<version>_jf12.zip`, attaches it
  to a GitHub release, then prepends an entry (targetAbi `12.0.0.0`) to
  `manifest.json` and commits it to `main` as `github-actions[bot]`. So the manifest
  is release-driven — the tag is the trigger, not the source-tree version fields.
- The tag version can therefore run ahead of `csproj`/`build.yaml` if a release is
  cut without bumping those files. Bump `AssemblyVersion`/`FileVersion`/`Version`
  (csproj) and `build.yaml` in the same change set as the tag to keep them aligned.
- **`v1.3.0.0` is burned**: it was tagged and then manually removed from
  `manifest.json` (commit `9066f8c`). Do not reuse it — 1.2.0.0 was followed by
  1.4.0.0.

## Conventions

- Keep the plugin GUID consistent across all metadata files (`build.yaml`,
  `manifest.json`, `Plugin.cs`, `configPage.html`).
- Jellyfin assemblies are compile-time only (`<ExcludeAssets>runtime</ExcludeAssets>`);
  never bundle them.
- Update `progress.md` with an entry per change set.
