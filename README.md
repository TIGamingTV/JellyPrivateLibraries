# JellyPrivateLibraries

A Jellyfin server plugin that **restricts which media each user sees** inside their libraries.

A title becomes visible to a user in one of two ways:

1. **They requested it through Jellyseerr** — the plugin receives a Jellyseerr webhook and auto-grants the requester access when the media lands in the library. A scheduled API sync also backfills requests made *before* the plugin was installed.
2. **They added it from the home-screen widget** — a button injected into the web UI opens a dialog where the user searches the library and picks titles to make visible.

The plugin is **opt-in**: by default no one is restricted and everyone sees the full library. A user turns on **"Restrict my library"** in the widget when they want it, and can turn it back off anytime. (Admins can enable a *mandatory mode* in the plugin config to restrict every user automatically.)

## How it works

Jellyfin's parental controls include a per-user **allowed-tags whitelist** (`UserPolicy.AllowedTags`): when set, a user only sees items whose metadata `Tags` contain one of those tags, enforced server-side across every client. This plugin builds on that:

- Each user gets a unique **personal tag**, e.g. `jpl:<userId>`, and their policy's `AllowedTags` is set to that tag.
- **Granting** a title to a user adds their personal tag to the item's `Tags` (and locks the `Tags` field so metadata refreshes don't wipe it).
- **Disabling** a user's restriction removes their personal tag from `AllowedTags`, so the whitelist no longer applies to them.

The plugin configuration is the **source of truth** for who is restricted and what they're granted; the tags on items are derived from it and re-applied by a scheduled reconcile task (every 30 min + on startup) and in real time when new media is imported.

## Components

| Area | File |
|---|---|
| Plugin entry point | `Jellyfin.Plugin.PrivateLibraries/Plugin.cs` |
| Configuration + admin page | `Configuration/PluginConfiguration.cs`, `Configuration/configPage.html` |
| Core logic (tags + policies) | `Services/RestrictionManager.cs` |
| Real-time grant application | `Services/ItemAddedListener.cs` |
| Widget script injection | `Services/ScriptInjector.cs` |
| Periodic reconcile | `ScheduledTasks/ReconcileTask.cs` |
| Jellyseerr backfill sync | `Services/JellyseerrClient.cs`, `Services/JellyseerrSyncService.cs`, `ScheduledTasks/JellyseerrSyncTask.cs` |
| REST API + Jellyseerr webhook | `Api/RestrictionController.cs` |
| Home-screen widget | `Web/private-libraries.js` |
| DI registration | `PluginServiceRegistrator.cs` |

## Building

Requires the .NET 9 SDK (Jellyfin 10.11 targets .NET 9).

```bash
dotnet build Jellyfin.Plugin.PrivateLibraries/Jellyfin.Plugin.PrivateLibraries.csproj -c Release
```

The output `Jellyfin.Plugin.PrivateLibraries.dll` (in `bin/Release/net9.0/`) is the plugin. GitHub Actions (`.github/workflows/build.yml`) also builds it on every push.

> The project targets `Jellyfin.Controller` `10.11.*`. Match this to your server version if you run something different (the allowed-tags whitelist requires Jellyfin **10.9+**).

## Installing

### Option A — via plugin repository URL (recommended)

1. In Jellyfin: **Dashboard → Plugins → Repositories → +** and add this URL:

   ```
   https://raw.githubusercontent.com/TIGamingTV/JellyPrivateLibraries/main/manifest.json
   ```

2. Go to **Catalog**, find **Private Libraries** under *General*, and install it.
3. Restart Jellyfin when prompted.
4. Open **Dashboard → Plugins → Private Libraries** to configure.
5. Refresh the web UI in your browser — the widget button (a video-library icon) appears in the top header.

Releases are produced by `.github/workflows/release.yml` (triggered by pushing a `v*` tag or running the workflow manually); it builds the DLL, publishes a GitHub Release zip, and updates `manifest.json` on `main` with the download URL and MD5 checksum.

### Option B — manual

1. Copy `Jellyfin.Plugin.PrivateLibraries.dll` into a folder named `Private Libraries` under your Jellyfin `plugins/` directory (e.g. `/config/plugins/Private Libraries/`).
2. Restart Jellyfin, then configure as above.

## Jellyseerr webhook setup

In **Jellyseerr → Settings → Notifications → Webhook**:

- **Webhook URL:** `https://<your-jellyfin>/PrivateLibraries/Webhook`
- **Authorization Header:** leave empty (auth is via the payload secret).
- **JSON Payload:**

```json
{
  "notification_type": "{{notification_type}}",
  "secret": "<the secret you set in the plugin config>",
  "media": {
    "media_type": "{{media_type}}",
    "tmdbId": "{{media_tmdbid}}",
    "tvdbId": "{{media_tvdbid}}"
  },
  "request": {
    "username": "{{requestedBy_username}}",
    "email": "{{requestedBy_email}}"
  }
}
```

Jellyseerr's **default** JSON payload template also works, as long as you add the `secret`
field to it — the webhook reads the requester from either `request.username` (above) or
`request.requestedBy_username` (the default template's field name), and accepts the provider
ids quoted or unquoted.

Enable the **Request Approved**, **Request Automatically Approved**, and **Media Available** notification types. The requester is matched to a Jellyfin user by **username** (this matches automatically for Jellyfin-authenticated Jellyseerr accounts).

If nothing is granted, the Jellyfin log says why — it names the notification type it ignored,
whether the requester field was missing, whether the username matched no Jellyfin user, and
whether both provider ids were empty.

## Syncing existing Jellyseerr requests

The webhook only reports **new** activity, so anything requested before the plugin was
installed (or while the webhook was misconfigured) is invisible to it — a user who turns
restriction on would lose their whole request history. The sync closes that gap by reading
the requests already stored in Jellyseerr over its REST API.

1. In **Jellyseerr → Settings → General**, copy the **API Key**.
2. In **Dashboard → Plugins → Private Libraries**, fill in the **Jellyseerr URL** (as
   reachable from the Jellyfin server, e.g. `http://jellyseerr:5055`) and paste the API key.
3. Press **Save & test connection** to verify, then **Save & sync now** to backfill.

After that it runs on its own every 12 hours — **Dashboard → Scheduled Tasks → Sync
Jellyseerr requests** — and you can trigger it there too. Leave the API key empty to keep
using the webhook only.

Details:

- Only **approved** and **completed** requests are granted, matching what the webhook does.
  Declined and failed requests are never granted; still-pending ones are opt-in via a
  checkbox.
- Grants are created with the same identity the webhook uses, so the two paths dedupe
  against each other — running the sync repeatedly, or after the webhook already granted a
  title, creates no duplicates.
- Requesters are mapped to Jellyfin users by **linked Jellyfin user id** first (present when
  Jellyseerr authenticates against Jellyfin), then by Jellyfin username, Jellyseerr username
  and display name. Accounts that match nothing are reported in the sync result and the log.
- As with the webhook, a grant for media that isn't in the library yet stays pending and is
  applied automatically once the title is imported.

## Caveats

- **Empty library once restricted:** because the whitelist hides everything untagged, a user who turns restriction on sees nothing until they're granted a title or one is requested for them. (By default users are unrestricted, so this only applies after they opt in — or if the admin enables mandatory mode.)
- **Script injection edits `index.html`** in the Jellyfin web root. The server process needs write access to that directory; if it doesn't, the restriction still works but the widget button won't appear. Some setups reset the web root on update — re-injection happens automatically on each startup.
- **Requester matching** relies on the Jellyseerr account being linked to a Jellyfin account or sharing its username. Unmatched requests are logged and skipped.
- **The sync grants an entire request history.** Backfilling a long-running Jellyseerr instance can hand a user a large number of titles at once; review the result summary after the first run.
- The widget is injected JavaScript against Jellyfin's web internals and may need touch-ups across major web-UI versions.

## License

MIT — see [LICENSE](LICENSE).
