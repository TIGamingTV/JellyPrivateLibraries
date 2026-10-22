# Usage

- [How it works](#how-it-works)
- [For users: My Private Library](#for-users-my-private-library)
- [Hidden media (admins)](#hidden-media-admins)
- [Granting titles through Jellyseerr](#granting-titles-through-jellyseerr)
- [Scheduled tasks](#scheduled-tasks)
- [Limitations](#limitations)

## How it works

Jellyfin user accounts have parental-control filters for **allowed tags** and **blocked
tags**. The plugin manages both for you:

- Every user gets a personal tag, `jpl:<user id>`. **Granting** a title to a user adds that
  tag to the movie or series (and locks the item's *Tags* field so metadata refreshes don't
  remove it).
- When a user's restriction is **on**, their personal tag is added to their *allowed tags*,
  so Jellyfin shows them only items carrying it. Turning it **off** removes it again.
- **Hidden** titles get the tag `jpl:hidden`, which is added to the *blocked tags* of every
  non-administrator. Blocked tags win over allowed tags.

Other allowed/blocked tags you set on a user yourself are left in place. The filtering is
done by the Jellyfin server, so it applies in every Jellyfin app, not only the web UI.

## For users: My Private Library

The **My Private Library** button is a video-library icon in the header of the Jellyfin web
UI, next to the search and user icons. It is not shown on the admin dashboard, in the video
player or on the login page.

The button exists only in the **web UI**. Grants and restriction made there apply in all
Jellyfin apps.

### Add titles

1. Click **My Private Library**.
2. Under **Add titles**, type part of a title. Up to 50 matching movies and series from the
   whole library are listed, even ones you can't currently see.
3. Click **Add** next to a title. It appears under **My titles**.

### Turn the restriction on or off

1. In the dialog, use the switch **Restrict my library to selected & requested titles**.
2. Close the dialog (✕ or click outside it). The page reloads.

- **On:** you see only the titles under **My titles**. With an empty list, your libraries
  are empty.
- **Off:** you see the whole library again. Your list is kept for next time.

### Remove a title

Click **Remove** next to the title under **My titles**, then close the dialog. This also
works for titles granted through Jellyseerr, but if the request still exists in
Jellyseerr and the [history sync](configuration.md#jellyseerr-history-sync) is enabled,
the next sync grants it again.

### Expected result

After closing the dialog, home screen and library views reflect your choice. Other
Jellyfin apps pick up the change the next time they load the library. If a change fails,
the dialog shows a red message such as `Adding title failed (HTTP 500): …`.

## Hidden media (admins)

Hide a movie or series from **every user except administrators**, independently of anyone's
personal restriction — a hidden title stays hidden even if it was granted to the user.

1. Open **Dashboard → Plugins → Private Libraries** and scroll to **Hidden media**.
2. Type in **Find a title to hide** and click **Hide** next to the title.
3. It appears under **Currently hidden**. To reverse it, click **Unhide**.

Changes take effect immediately and need no **Save**. Hiding and unhiding update every
user account; the reconcile task re-applies them afterwards, including for users created
later.

Only whole movies and series can be hidden. Administrator accounts are never affected.

## Granting titles through Jellyseerr

Once the [webhook](configuration.md#jellyseerr-webhook) (and optionally the
[history sync](configuration.md#jellyseerr-history-sync)) is set up, nothing more is needed
from users:

1. A user requests a movie or series in Jellyseerr.
2. When the request is approved (or auto-approved, or becomes available), the title is
   granted to the Jellyfin user with the same username.
3. If the title is not in the library yet, the grant waits. When Jellyfin imports it, the
   grant is applied right away (and at the latest by the next reconcile run).
4. The title appears in the user's **My titles** list and, if their restriction is on, in
   their libraries.

Grants are recorded for the user whether or not their restriction is on, so turning the
restriction on later does not lose their request history.

## Scheduled tasks

Both tasks are in **Dashboard → Scheduled Tasks** under the *Private Libraries* category,
where you can run them manually or change their triggers.

| Task | Default schedule | What it does |
|---|---|---|
| **Reconcile Private Libraries** | On startup and every 30 minutes | Applies mandatory mode, re-applies every user's allowed tags and every grant (including grants whose media was imported later or whose file was replaced), re-applies hidden titles, and removes leftover plugin tags that no longer belong to a grant. |
| **Sync Jellyseerr requests** | Every 12 hours | Runs the Jellyseerr history sync. Does nothing unless the sync is enabled and the Jellyseerr URL and API key are set. |

## Limitations

- **Not a security boundary.** Users can switch their own restriction off and add any
  movie or series, even in mandatory mode, and the widget search lists titles from the
  whole library. Use hidden media or Jellyfin's per-user library access for content that
  must stay inaccessible.
- **Movies and series only.** Only these can be granted or hidden. Because a restricted
  user sees only items carrying their tag, other content (for example music) is not shown
  to them while the restriction is on.
- **No per-user controls for admins.** Admins cannot switch a specific user's restriction
  from the plugin page; users do that themselves, or admins use mandatory mode.
- **Web UI only.** The button exists only in the Jellyfin web UI. It depends on
  jellyfin-web's page structure and may need an update after major web-UI changes.
- **Username matching.** Jellyseerr requests are matched to Jellyfin users by account
  link or username; unmatched requests are skipped and logged.
