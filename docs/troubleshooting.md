# Troubleshooting & maintenance

- [Where to find logs](#where-to-find-logs)
- [Widget button does not appear](#widget-button-does-not-appear)
- [A restricted user sees nothing, or not what they expect](#a-restricted-user-sees-nothing-or-not-what-they-expect)
- [Jellyseerr requests are not granted](#jellyseerr-requests-are-not-granted)
- [Manual installation](#manual-installation)
- [Updating](#updating)
- [Uninstalling](#uninstalling)
- [Jellyfin 10.11 servers](#jellyfin-1011-servers)

## Where to find logs

- **Dashboard → Logs** in the Jellyfin web UI.
- In Docker: `docker logs <jellyfin-container>`; log files are also written to Jellyfin's
  log directory (`/config/log` in the official image).

Plugin messages mention *Private Libraries*, *PrivateLibraries* or *Jellyseerr*.

## Widget button does not appear

1. **Reload the page** with a hard refresh (or clear the site cache). The button is
   loaded by Jellyfin's `index.html`, which browsers cache.
2. **Check where you are looking.** The button is shown only in the web UI, in the top
   header of normal pages. It is intentionally hidden on the admin dashboard (including the
   plugin's own settings page), in the video player and on the login page.
3. **Check the Jellyfin log from the last startup** for one of these messages:

   | Log message | Meaning |
   |---|---|
   | `Registered the Private Libraries widget script injection with the File Transformation plugin.` | Injection is handled by File Transformation; go back to step 1. |
   | `Injected Private Libraries widget script into <path>` | The plugin patched `index.html` directly; go back to step 1. |
   | `File Transformation plugin not found; falling back to directly patching index.html.` | Normal without File Transformation; a following message tells you whether patching worked. |
   | `Could not inject the Private Libraries widget script into index.html. …` | Jellyfin cannot write to its web directory. |
   | `index.html not found at <path>; skipping widget injection` | Jellyfin's web directory was not found where expected. |

4. **If Jellyfin cannot write its web directory** (common when the server runs as a
   non-root user or the web files are read-only), install the
   [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
   plugin and restart Jellyfin. With it, the button is added in memory and nothing is written
   to disk. Restriction and grants work either way.

When `index.html` is patched directly, a Jellyfin update that replaces the web files
removes the patch; the plugin re-applies it on every Jellyfin startup.

## A restricted user sees nothing, or not what they expect

- **Empty library right after turning restriction on** is expected when **My titles** is
  empty. Add titles, or turn the restriction off.
- **A title is in Jellyseerr but not in My titles:** it is not in the Jellyfin library yet
  (the grant is applied on import), or Jellyfin's metadata for the item has neither the TMDB
  nor the TVDB id from the request. Check the item's external ids in Jellyfin (e.g. via
  *Identify*) and run **Reconcile Private Libraries**.
- **A granted title is still invisible:** it may be in the admin **Hidden media** list —
  hidden always wins.
- **Music or other non-video libraries disappeared:** only movies and series can be
  granted, so other content is not shown while a user's restriction is on.
- **Mandatory mode does not seem to apply:** it applies on the next *Reconcile Private
  Libraries* run (startup / every 30 minutes) and only to users without an existing choice.
  See [Mandatory mode](configuration.md#mandatory-mode).
- **Something looks out of sync:** run **Dashboard → Scheduled Tasks → Reconcile Private
  Libraries**. It re-applies everything from the plugin configuration.

## Jellyseerr requests are not granted

Check the Jellyfin log right after the request was approved. The plugin logs why a webhook
did not grant anything:

| Log message | Fix |
|---|---|
| *(no plugin message at all)* | Jellyseerr is not calling Jellyfin. Check the webhook URL is reachable from the Jellyseerr host/container, the agent is enabled, and the notification types are selected. Use Jellyseerr's **Test** button. |
| `Rejected Jellyseerr webhook: no shared secret configured` | Enter a **Webhook shared secret** in the plugin and click **Save**. |
| `Rejected Jellyseerr webhook: bad secret` | Make the payload's `secret` exactly equal the plugin setting. |
| `Ignoring Jellyseerr notification of type …` | Expected for types other than approved / auto-approved / available. |
| `… carried no requester username …` | The payload has no `request.username` or `request.requestedBy_username`. Use the template from [Configuration](configuration.md#2-configure-the-webhook-in-jellyseerr). |
| `Jellyseerr requester '<name>' has no matching Jellyfin user; ignoring` | Create a Jellyfin user with that exact name, or rename one. |
| `… had no usable provider id …` | The payload's `media.tmdbId` and `media.tvdbId` were both empty. Check the payload template. |

For requests made **before** the webhook worked, use the
[history sync](configuration.md#jellyseerr-history-sync). Its result lists the Jellyseerr
accounts that matched no Jellyfin user.

## Manual installation

Use this only if you cannot use the plugin repository. Manual installs are not updated
automatically.

1. Download `private-libraries_<version>_jf12.zip` from the
   [Releases page](https://github.com/TIGamingTV/JellyPrivateLibraries/releases).
2. Stop Jellyfin.
3. Create a folder for the plugin in Jellyfin's `plugins` directory, for example
   `/config/plugins/Private Libraries/` in the official Docker image.
4. Extract the zip into it (it contains `Jellyfin.Plugin.PrivateLibraries.dll` and the
   plugin image).
5. Start Jellyfin and [verify the installation](../README.md#3-verify).

## Updating

When installed from the plugin repository, updates appear in **Dashboard → Plugins**
(Jellyfin may also install them automatically, depending on your server settings). Install
the update, restart Jellyfin, and reload the web page.

Settings, user choices, grants and hidden titles are kept in the plugin configuration file
(see [Where the configuration is stored](configuration.md#where-the-configuration-is-stored))
and survive updates.

## Uninstalling

The restriction lives in Jellyfin's own user settings, so it does **not** disappear by
itself when the plugin is removed. To uninstall cleanly:

1. On the plugin page, **Unhide** every title under **Hidden media**.
2. Ask restricted users to switch **Restrict my library** off (or do step 5 for them).
3. Uninstall the plugin in **Dashboard → Plugins** and restart Jellyfin.
4. If you don't want to keep the plugin's data, delete
   `plugins/configurations/Jellyfin.Plugin.PrivateLibraries.xml` if it still exists.
5. For each user, open **Dashboard → Users → (user) → Parental Control** and remove any
   remaining allowed or blocked tags starting with `jpl:` (or your custom prefix).

Tags starting with `jpl:` may remain on movies and series. They are harmless once no user
filters on them. If the plugin had patched `index.html` directly, its script reference stays
until Jellyfin's web files are next replaced; it loads nothing once the plugin is gone.

## Jellyfin 10.11 servers

Jellyfin 10.11.x is not supported by current releases, and the plugin repository only
offers builds for Jellyfin 12. The last release built for 10.11 is **v1.9.3.0**. To use it:

1. Open the [v1.9.3.0 release](https://github.com/TIGamingTV/JellyPrivateLibraries/releases/tag/v1.9.3.0).
2. Download **`private-libraries_1.9.3.0.zip`** — the file *without* `_jf12` is the 10.11
   build.
3. Install it as described in [Manual installation](#manual-installation).

That version receives no further updates or fixes, and this documentation describes the
current release.
