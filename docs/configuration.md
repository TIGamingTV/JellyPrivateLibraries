# Configuration

All plugin settings live on one page: **Dashboard → Plugins → Private Libraries**.
The plugin needs no configuration for the user widget to work; the settings below control
mandatory mode, the tag naming, and the optional Jellyseerr integration.

- [Settings reference](#settings-reference)
- [Jellyseerr webhook](#jellyseerr-webhook)
- [Jellyseerr history sync](#jellyseerr-history-sync)
- [How requesters are matched to Jellyfin users](#how-requesters-are-matched-to-jellyfin-users)
- [Where the configuration is stored](#where-the-configuration-is-stored)

## Settings reference

Settings are applied when you click **Save** (or one of the *Save & …* buttons). No
Jellyfin restart is needed for any of them.

| Setting (UI label) | Default | Accepted values | Effect |
|---|---|---|---|
| **Personal tag prefix** | `jpl` | Any text; blank falls back to `jpl` | Each user's personal tag is `<prefix>:<user id>` and hidden titles get `<prefix>:hidden`. See the warning below. |
| **Mandatory mode: restrict every user automatically** | Off | On / Off | See [Mandatory mode](#mandatory-mode). |
| **Jellyseerr URL** | *(empty)* | Absolute `http://` or `https://` URL, e.g. `http://jellyseerr:5055` | Used only by the history sync and the connection test. Must be reachable *from the Jellyfin server*. A trailing `/` or `/api/v1` is accepted. |
| **Webhook shared secret** | *(empty)* | Any text (use a long random string) | Must match the `secret` field of the Jellyseerr webhook payload. **While empty, every webhook is rejected.** |
| **Jellyseerr API key** | *(empty)* | The API key from Jellyseerr | Enables the history sync. Leave empty to use the webhook only. Sensitive — treat it like a password. |
| **Run the Jellyseerr sync automatically** | On | On / Off | On: the *Sync Jellyseerr requests* scheduled task runs every 12 hours. Off: the task does nothing; only **Save & sync now** runs a sync. |
| **Also grant requests that are still awaiting approval** | Off | On / Off | Off: the sync grants only *approved* and *completed* requests. On: *pending* requests are granted too. Declined and failed requests are never granted. |

> **Warning — Personal tag prefix:** change it only before anyone uses the plugin, and only
> if `jpl:` clashes with tags you already use. Changing it later is **not migrated**:
> existing users keep their old tag, and titles that are already hidden keep the old
> hidden tag, which the *Unhide* button can then no longer remove.

### Mandatory mode

- **Off (default):** the plugin is opt-in. A user is restricted only after they switch
  on *Restrict my library* in the widget.
- **On:** every user the plugin has no record of yet starts **restricted** — they see
  nothing until a title is granted to them. The plugin creates a record for a user the
  first time they use the widget toggle, add a title, or receive a Jellyseerr grant;
  users who already have a record keep their current setting.

Mandatory mode is applied by the *Reconcile Private Libraries* scheduled task (on startup
and every 30 minutes). To apply it immediately, run that task from **Dashboard →
Scheduled Tasks**.

Users can still turn their own restriction off from the widget while mandatory mode is
on. The setting changes the *default*, it does not lock users in.

Turning mandatory mode **off** again does not un-restrict users who were enrolled while it
was on — each of them has to switch their own restriction off in the widget.

### Other page sections

- **Last sync** — time and summary of the most recent Jellyseerr sync.
- **Jellyseerr webhook setup** — shows the webhook URL for *your* server and the payload
  template to paste into Jellyseerr.
- **Hidden media** — hide titles from all non-admins. Changes there take effect
  immediately and do not need **Save**. See [Usage → Hidden media](usage.md#hidden-media-admins).

## Jellyseerr webhook

The webhook grants a title to the user who requested it as soon as Jellyseerr reports the
request as approved or available. If the media is not in the Jellyfin library yet, the
grant waits and is applied automatically when the title is imported.

**Before you start:** Jellyseerr must be able to reach your Jellyfin server over HTTP(S),
and each Jellyseerr requester must have a Jellyfin account with the same username (true
automatically for Jellyseerr accounts that sign in with Jellyfin).

### 1. Set the secret in Jellyfin

1. Open **Dashboard → Plugins → Private Libraries**.
2. Enter a long random value in **Webhook shared secret**.
3. Click **Save**.
4. Note the URL shown under **Jellyseerr webhook setup** — it is your server address
   followed by `/PrivateLibraries/Webhook`, for example
   `https://jellyfin.example.com/PrivateLibraries/Webhook`. Use an address Jellyseerr can
   reach (for example `http://jellyfin:8096/PrivateLibraries/Webhook` when both run on the
   same Docker network). If Jellyfin is configured with a base URL path, include it.

### 2. Configure the webhook in Jellyseerr

In **Jellyseerr → Settings → Notifications → Webhook**:

1. Enable the webhook agent.
2. **Webhook URL:** the URL from step 1.
3. **Authorization Header:** leave empty. Authentication uses the `secret` in the payload.
4. **JSON Payload:** replace it with the template below, putting your secret in `secret`:

   ```json
   {
     "notification_type": "{{notification_type}}",
     "secret": "REPLACE_WITH_YOUR_WEBHOOK_SHARED_SECRET",
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

   Jellyseerr's default payload template also works if you add a top-level `"secret"`
   field to it: the plugin also reads the requester from `request.requestedBy_username`
   and accepts provider ids with or without quotes.
5. **Notification types:** enable **Request Approved**, **Request Automatically
   Approved** and **Media Available**. Other types are accepted but ignored.
6. Save, then press Jellyseerr's **Test** button.

### 3. Verify

- The test notification should succeed. The plugin answers a test with HTTP 200 and the
  message `Private Libraries webhook OK`.
- After a real request is approved, the Jellyfin log contains
  `Granted Jellyseerr request to user <name> (tmdb=…, tvdb=…)`, and the title appears in
  that user's **My titles** list once it is in the library.

### Webhook responses

| Response | Meaning |
|---|---|
| HTTP 403 | No **Webhook shared secret** is saved in the plugin. |
| HTTP 401 | The `secret` in the payload does not match the plugin setting. |
| HTTP 400 | The payload is not valid JSON. |
| HTTP 200 with `Ignored notification type …` | A notification type the plugin does not act on. |
| HTTP 200 with `No requester` / `No matching Jellyfin user` / `No usable provider id in payload` | Nothing was granted; the Jellyfin log names the reason. See [Troubleshooting](troubleshooting.md#jellyseerr-requests-are-not-granted). |
| HTTP 200 with `Granted` | Grant recorded (applied now, or when the media is imported). |

## Jellyseerr history sync

The webhook only sees *new* events. The sync reads every request already stored in
Jellyseerr, so requests made before the plugin was installed (or while the webhook was
misconfigured) are granted too. Grants from the sync and the webhook are de-duplicated,
so running both, or running the sync repeatedly, creates no duplicates.

1. In **Jellyseerr → Settings → General**, copy the **API Key**.
2. In **Dashboard → Plugins → Private Libraries**:
   - **Jellyseerr URL:** the Jellyseerr address as reachable from the Jellyfin server,
     e.g. `http://jellyseerr:5055`.
   - **Jellyseerr API key:** paste the key.
3. Click **Save & test connection**. Success shows
   `Connected to Jellyseerr <version>. API key accepted.`
4. Click **Save & sync now**. When it finishes, a summary is shown, for example:
   `120 request(s) read, 85 grant(s) created, 0 already present, 30 skipped by status, 5 with no matching user, 0 with no provider id`,
   followed by the Jellyseerr accounts that matched no Jellyfin user.

After that, the *Sync Jellyseerr requests* task runs every 12 hours while **Run the
Jellyseerr sync automatically** is on. You can also start it from **Dashboard → Scheduled
Tasks**. It does not run at startup.

**What gets granted:** approved and completed requests (plus pending ones if **Also grant
requests that are still awaiting approval** is on). A TV request that carries both a TMDB
and a TVDB id produces a grant for each, so the series is found whichever id Jellyfin
matched it on.

> **Note:** the first sync on a long-running Jellyseerr instance can grant a user many
> titles at once. Review the summary after the first run.

Errors shown by the buttons:

| Message | Fix |
|---|---|
| `Set the Jellyseerr URL and API key … before syncing.` | Fill in both fields. |
| `The Jellyseerr URL must be an absolute http(s) URL …` | Include `http://` or `https://`. |
| `Jellyseerr rejected the API key (HTTP 401/403) …` | Re-copy the key from Jellyseerr → Settings → General. |
| `Jellyseerr returned 404 for … Check the Jellyseerr URL points at the site root.` | Use the Jellyseerr root URL, not a sub-page. |
| `A Jellyseerr sync is already running.` | Wait for the running sync (scheduled or manual) to finish. |

## How requesters are matched to Jellyfin users

| Path | Matching order |
|---|---|
| Webhook | Jellyfin user whose name equals the payload's requester username. |
| History sync | 1. Linked Jellyfin user id (present when Jellyseerr uses Jellyfin sign-in); 2. linked Jellyfin username; 3. Jellyseerr username; 4. Jellyseerr display name. |

Requests whose requester matches no Jellyfin user are skipped and logged. Create or rename
the Jellyfin user (or link the Jellyseerr account) and run the sync again.

## Where the configuration is stored

The plugin configuration — settings, every user's restriction state, all grants and the
hidden-title list — is the source of truth. Jellyfin stores it as
`plugins/configurations/Jellyfin.Plugin.PrivateLibraries.xml` inside the Jellyfin
configuration/data directory (for the official Docker image, under `/config`). Back this
file up together with the rest of your Jellyfin configuration.

The tags on library items and the allowed/blocked tags on user accounts are derived from
this file and re-applied by the reconcile task; editing them by hand is overwritten.
