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
- `Services/ScriptInjector.cs` — patches `index.html` (Intro-Skipper pattern).
- `Web/private-libraries.js` — injected widget (vanilla JS, no build step).
- `ScheduledTasks/ReconcileTask.cs` — startup + 30-min interval reconcile.

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
Jellyfin (10.11), create two users, confirm each starts restricted, grant a title
via the widget, toggle restriction off/on, and fire a Jellyseerr test/approved
webhook. See `progress.md` for history.

## Conventions

- Keep the plugin GUID and `targetAbi` consistent across all metadata files.
- Jellyfin assemblies are compile-time only (`<ExcludeAssets>runtime</ExcludeAssets>`);
  never bundle them.
- Update `progress.md` with an entry per change set.
