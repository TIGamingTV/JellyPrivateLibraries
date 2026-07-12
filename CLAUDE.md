# CLAUDE.md

Guidance for working in this repository.

## What this is

A **Jellyfin server plugin** (C# / .NET 9) that restricts each user to only the
media they requested via Jellyseerr or added from a home-screen widget. It is built
on Jellyfin's native per-user **allowed-tags whitelist** (`UserPolicy.AllowedTags`).

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
- `Services/ScriptInjector.cs` — patches `index.html` (Intro-Skipper pattern).
- `Web/private-libraries.js` — injected widget (vanilla JS, no build step).
- `ScheduledTasks/ReconcileTask.cs` — startup + 30-min interval reconcile.
- `Configuration/PluginConfiguration.cs` — the source of truth (`Users`,
  `Grants`, `HiddenItems`, `TagPrefix`, `RestrictNewUsersByDefault`,
  `SchemaVersion`) + config page (`configPage.html`).

## Jellyfin API notes (target 10.11 / master)

- `TaskTriggerInfo.Type` is the **enum** `TaskTriggerInfoType` (not a string) in
  10.11+. Older versions used string constants — do not "fix" it back.
- `IUserManager`: `GetUsers()`, `GetUserById`, `GetUserByName`, `GetUserDto`,
  `UpdatePolicyAsync`. There is **no** `GetUserPolicy` — read via `GetUserDto`.
- `ILibraryManager.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, ct)`.
- `MetadataField.Tags` is the lockable field for tags.

## Build & verify

```bash
dotnet build Jellyfin.Plugin.PrivateLibraries/Jellyfin.Plugin.PrivateLibraries.csproj -c Release
```

There is no unit test project yet. Manual verification: load the DLL into a test
Jellyfin (10.11), create two users, confirm each starts **unrestricted** (restriction
is opt-in since v1.0.0.4 — `RestrictNewUsersByDefault` defaults to `false`), toggle
restriction on from the widget and confirm the library then narrows to granted titles,
grant a title, toggle restriction off/on, and fire a Jellyseerr test/approved webhook.
See `progress.md` for history.

## Releasing

- `.github/workflows/release.yml` runs on a pushed `v*` tag (or manual dispatch):
  it builds, packages the DLL as `private-libraries_<version>.zip`, creates a
  GitHub release, then prepends a new entry to `manifest.json` and commits it to
  `main` as `github-actions[bot]`. So `manifest.json` is release-driven — the tag
  is the trigger, not the source-tree version fields.
- The tag version can therefore run ahead of `csproj`/`build.yaml` if a release is
  cut without bumping those files. As of 1.0.0.6 (audit fixes) that drift exists:
  `manifest.json` lists 1.0.0.6 while `csproj`/`build.yaml` still read 1.0.0.5.
  Bump `AssemblyVersion`/`FileVersion`/`Version` (csproj) and `build.yaml` in the
  same change set as the tag to keep them aligned.

## Conventions

- Keep the plugin GUID and `targetAbi` consistent across all metadata files.
- Jellyfin assemblies are compile-time only (`<ExcludeAssets>runtime</ExcludeAssets>`);
  never bundle them.
- Update `progress.md` with an entry per change set.
