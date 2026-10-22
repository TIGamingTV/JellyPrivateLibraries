# Development

Contributor notes. For installing and using the plugin, see the [README](../README.md).
Maintainer conventions are in [CLAUDE.md](../CLAUDE.md) and the change history in
[progress.md](../progress.md).

## Building

Requires the **.NET 10 SDK**.

```bash
dotnet build Jellyfin.Plugin.PrivateLibraries/Jellyfin.Plugin.PrivateLibraries.csproj -c Release
```

Output: `Jellyfin.Plugin.PrivateLibraries/bin/Release/net10.0/Jellyfin.Plugin.PrivateLibraries.dll`.
A clean build has 0 warnings and 0 errors. To try a build, install the DLL as described in
[Manual installation](troubleshooting.md#manual-installation).

Jellyfin assemblies (`Jellyfin.Controller`, pinned to `12.0.0`) are compile-time only and
are never shipped. The plugin is a single DLL with no extra dependencies; the release
workflow does not copy dependency assemblies, so do not add NuGet runtime dependencies.

There is no automated test project. Verify changes manually on a Jellyfin 12.0 test server
(see *Build & verify* in [CLAUDE.md](../CLAUDE.md)).

## Project layout

| Area | File (under `Jellyfin.Plugin.PrivateLibraries/`) |
|---|---|
| Plugin entry point, config page registration | `Plugin.cs` |
| Dependency-injection registration | `PluginServiceRegistrator.cs` |
| Settings model (source of truth) and admin page | `Configuration/PluginConfiguration.cs`, `Configuration/configPage.html` |
| Tag / user-policy logic | `Services/RestrictionManager.cs` |
| Apply grants when media is imported | `Services/ItemAddedListener.cs` |
| Web UI script injection | `Services/ScriptInjector.cs`, `Services/FileTransformationPayload.cs` |
| Web UI widget (vanilla JS, no build step) | `Web/private-libraries.js` |
| Jellyseerr API sync | `Services/JellyseerrClient.cs`, `Services/JellyseerrModels.cs`, `Services/JellyseerrSyncService.cs` |
| Scheduled tasks | `ScheduledTasks/ReconcileTask.cs`, `ScheduledTasks/JellyseerrSyncTask.cs` |
| REST API and webhook | `Api/RestrictionController.cs` |

## REST API

All routes are under `/PrivateLibraries`.

| Method & route | Auth | Purpose |
|---|---|---|
| `GET ClientScript` | Anonymous | Serves the widget script. |
| `GET Me` | Any user | Caller's restriction state and grant count. |
| `POST Me/Restriction` | Any user | Body `{ "Enabled": true }` — turn the caller's restriction on/off. |
| `GET Me/Grants` | Any user | Caller's granted items. |
| `POST Me/Grants` | Any user | Body `{ "ItemId": "<id>" }` — grant an item to the caller. |
| `DELETE Me/Grants/{itemId}` | Any user | Revoke a grant from the caller. |
| `GET Search?query=` | Any user | Search movies/series in the whole library (max 50). |
| `GET Hidden` | Admin | List hidden items. |
| `GET Hidden/Search?query=` | Admin | Search items to hide (max 50). |
| `POST Hidden` | Admin | Body `{ "ItemId": "<id>" }` — hide an item. |
| `DELETE Hidden/{itemId}` | Admin | Unhide an item. |
| `POST Jellyseerr/Test` | Admin | Test the Jellyseerr URL and API key. |
| `POST Jellyseerr/Sync` | Admin | Run the history sync now. |
| `POST Webhook` | Anonymous; payload `secret` | Jellyseerr webhook receiver. |

## Releasing

`.github/workflows/release.yml` runs when a `v*` tag is pushed (or on manual dispatch). It
builds the plugin, packages `private-libraries_<version>_jf12.zip` (DLL + plugin image),
creates the GitHub release, and prepends a `targetAbi 12.0.0.0` entry with the zip's URL and
MD5 checksum to `manifest.json`, committing it to `main`.

Before tagging, bump `AssemblyVersion`, `FileVersion` and `Version` in the `.csproj` and
`version` in `build.yaml` to the tag's version. Never reuse burned versions (see
[CLAUDE.md](../CLAUDE.md#releasing)).

`.github/workflows/build.yml` builds pushes to `main` and `claude/**` branches and pull
requests against `main`, and uploads the DLL as a workflow artifact.
