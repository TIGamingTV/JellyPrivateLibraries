# CLAUDE.md

Guidance for working in this repository.

## What this is

A **Jellyfin server plugin** (C#, multi-targeting .NET 9 for Jellyfin 10.11 and
.NET 10 for Jellyfin 12.0) that restricts each user to only the media they requested
via Jellyseerr or added from a home-screen widget. It is built on Jellyfin's native
per-user **allowed-tags whitelist** (`UserPolicy.AllowedTags`).

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
  `build.jf12.yaml`, `manifest.json`, `manifest-jf12.json`, and `configPage.html`).
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

## Supported server versions (multi-targeting)

The project **multi-targets one TFM per supported Jellyfin line**, from identical
source — only the reference assemblies differ:

| TFM | Jellyfin | `Jellyfin.Controller` | Manifest | Release zip |
|---|---|---|---|---|
| `net9.0` | 10.11.x | `10.11.*` | `manifest.json` | `private-libraries_<v>.zip` |
| `net10.0` | 12.0.x | `12.0.0-rc7` (pinned) | `manifest-jf12.json` | `private-libraries_<v>_jf12.zip` |

- Jellyfin **12.0 is 10.12 renamed** (the leading `10.` was dropped); the plugin API
  is unchanged from 10.11, which is why no source changes were needed. Do not assume
  a 12.x API break without checking.
- The 12.0 reference is **pinned to an exact RC on purpose**: a stray `12.0.0-rcrc3`
  package on nuget.org sorts *above* `rc7` under SemVer prerelease ordering, so a
  `12.0.*-*` float would silently resolve to it. Move to `12.0.*` once 12.0.0 is
  stable.
- Two manifests are required because Jellyfin's `VersionInfo` has only `targetAbi`,
  which is a **minimum** server version, and no `maxAbi`. One manifest cannot say
  "10.11 only" — a 12.x server would match the 10.11 entry and install the .NET 9 DLL.
- `build.yaml` / `build.jf12.yaml` are the descriptors for the two artifacts. Nothing
  in CI consumes them; they are metadata and must be kept in step with the manifests.

## Jellyfin API notes (targets 10.11 and 12.0)

Verified identical across `Jellyfin.Controller` 10.11.11 and 12.0.0-rc7 — the whole
plugin compiles against both with zero warnings.

- `TaskTriggerInfo.Type` is the **enum** `TaskTriggerInfoType` (not a string) in
  10.11+. Older versions used string constants — do not "fix" it back.
- `IUserManager`: `GetUsers()`, `GetUserById`, `GetUserByName`, `GetUserDto`,
  `UpdatePolicyAsync`. There is **no** `GetUserPolicy` — read via `GetUserDto`.
- `ILibraryManager.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, ct)`.
- `MetadataField.Tags` is the lockable field for tags.

## Build & verify

Requires the .NET 9 **and** .NET 10 SDKs; one `build` produces both DLLs.

```bash
dotnet build Jellyfin.Plugin.PrivateLibraries/Jellyfin.Plugin.PrivateLibraries.csproj -c Release
# -> bin/Release/net9.0/...dll   (Jellyfin 10.11)
# -> bin/Release/net10.0/...dll  (Jellyfin 12.0)
```

Add `-f net9.0` / `-f net10.0` to build a single target. A clean build is **0 warnings,
0 errors** on both; treat any new warning as a regression.

There is no unit test project yet. Manual verification: load the DLL into a test
Jellyfin (10.11), create two users, confirm each starts **unrestricted** (restriction
is opt-in since v1.0.0.4 — `RestrictNewUsersByDefault` defaults to `false`), toggle
restriction on from the widget and confirm the library then narrows to granted titles,
grant a title, toggle restriction off/on, and fire a Jellyseerr test/approved webhook.
See `progress.md` for history.

## Releasing

- `.github/workflows/release.yml` runs on a pushed `v*` tag (or manual dispatch):
  it builds both TFMs, packages `private-libraries_<version>.zip` (net9.0) and
  `private-libraries_<version>_jf12.zip` (net10.0), attaches both to a single
  GitHub release, then prepends an entry to **both** `manifest.json` (targetAbi
  `10.11.0.0`) and `manifest-jf12.json` (targetAbi `12.0.0.0`) and commits them to
  `main` as `github-actions[bot]`. So the manifests are release-driven — the tag
  is the trigger, not the source-tree version fields.
- The tag version can therefore run ahead of `csproj`/`build.yaml`/`build.jf12.yaml`
  if a release is cut without bumping those files. Bump `AssemblyVersion`/
  `FileVersion`/`Version` (csproj) and both `build*.yaml` in the same change set as
  the tag to keep them aligned.
- **`v1.3.0.0` is burned**: it was tagged and then manually removed from
  `manifest.json` (commit `9066f8c`). Do not reuse it — 1.2.0.0 was followed by
  1.4.0.0.

## Conventions

- Keep the plugin GUID consistent across all metadata files. `targetAbi` is
  deliberately **not** uniform: `10.11.0.0` in `build.yaml`/`manifest.json`,
  `12.0.0.0` in `build.jf12.yaml`/`manifest-jf12.json`.
- Jellyfin assemblies are compile-time only (`<ExcludeAssets>runtime</ExcludeAssets>`);
  never bundle them.
- Update `progress.md` with an entry per change set.
