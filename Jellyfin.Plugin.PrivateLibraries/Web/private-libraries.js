/*
 * Private Libraries widget.
 * Injected into the Jellyfin web client. Adds a button to the top header that
 * opens a dialog where each user can toggle their own library restriction and
 * pick which media they want to see.
 */
(function () {
    'use strict';

    var BTN_ID = 'privateLibrariesButton';
    var OVERLAY_ID = 'privateLibrariesOverlay';
    var pendingReload = false;

    function getApiClient() {
        return window.ApiClient || (window.connectionManager && window.connectionManager.currentApiClient && window.connectionManager.currentApiClient());
    }

    // Use Jellyfin's ApiClient so the correct Authorization header is sent
    // (raw fetch with X-Emby-Token is rejected by Jellyfin 10.11).
    function apiGet(path) {
        var client = getApiClient();
        return client.ajax({ type: 'GET', url: client.getUrl('PrivateLibraries/' + path), dataType: 'json' });
    }

    function apiSend(method, path, body) {
        var client = getApiClient();
        var req = { type: method, url: client.getUrl('PrivateLibraries/' + path), dataType: 'json' };
        if (body !== undefined) {
            req.data = JSON.stringify(body);
            req.contentType = 'application/json';
        }
        return client.ajax(req);
    }

    function injectStyles() {
        if (document.getElementById('privateLibrariesStyles')) {
            return;
        }
        var css = ''
            + '#' + OVERLAY_ID + '{position:fixed;inset:0;background:rgba(0,0,0,.6);z-index:9999;display:flex;align-items:center;justify-content:center;}'
            + '.pl-dialog{background:#101418;color:#eee;width:min(680px,92vw);max-height:86vh;overflow:auto;border-radius:10px;padding:20px 22px;box-shadow:0 10px 40px rgba(0,0,0,.5);}'
            + '.pl-dialog h2{margin:0 0 4px;font-size:1.3em;}'
            + '.pl-dialog h3{margin:18px 0 8px;font-size:1.02em;opacity:.9;}'
            + '.pl-row{display:flex;align-items:center;gap:10px;padding:8px 0;border-bottom:1px solid #23292f;}'
            + '.pl-row:last-child{border-bottom:0;}'
            + '.pl-grow{flex:1;min-width:0;}'
            + '.pl-title{font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}'
            + '.pl-sub{font-size:.82em;opacity:.6;}'
            + '.pl-btn{background:#00a4dc;color:#fff;border:0;border-radius:6px;padding:7px 14px;cursor:pointer;font-size:.9em;}'
            + '.pl-btn.secondary{background:#2b333c;}'
            + '.pl-btn.danger{background:#b3403a;}'
            + '.pl-btn:disabled{opacity:.5;cursor:default;}'
            + '.pl-input{width:100%;box-sizing:border-box;padding:10px 12px;border-radius:6px;border:1px solid #2b333c;background:#181d22;color:#eee;font-size:.95em;}'
            + '.pl-toggle{display:flex;align-items:center;gap:12px;background:#181d22;border-radius:8px;padding:12px 14px;margin-bottom:6px;}'
            + '.pl-switch{position:relative;width:46px;height:26px;flex:0 0 auto;}'
            + '.pl-switch input{opacity:0;width:0;height:0;}'
            + '.pl-slider{position:absolute;inset:0;background:#555;border-radius:26px;transition:.2s;cursor:pointer;}'
            + '.pl-slider:before{content:"";position:absolute;height:20px;width:20px;left:3px;bottom:3px;background:#fff;border-radius:50%;transition:.2s;}'
            + '.pl-switch input:checked + .pl-slider{background:#00a4dc;}'
            + '.pl-switch input:checked + .pl-slider:before{transform:translateX(20px);}'
            + '.pl-close{float:right;background:none;border:0;color:#aaa;font-size:1.6em;cursor:pointer;line-height:1;}'
            + '.pl-muted{opacity:.6;font-size:.9em;padding:6px 0;}'
            + '.pl-error{background:#4a1f1f;color:#ffb4b4;border-radius:6px;padding:8px 12px;margin:8px 0;font-size:.88em;display:none;}'
            // Jellyfin 12's MUI toolbar buttons get their actual appearance from
            // emotion-injected CSS keyed to auto-generated hash classes, not from the
            // plain "MuiIconButton-root" global class name alone (that's only present
            // for identification). Borrowing just the class names left our injected
            // <button> with the browser's default button chrome (opaque face, border),
            // which read as a solid white box sitting on top of the dark header instead
            // of blending in like a real icon button, so reset it explicitly here and
            // pick up the real icon colour at runtime (see positionMuiButton).
            + '.pl-mui-btn{position:fixed;width:40px;height:40px;padding:8px;'
            + 'display:inline-flex;align-items:center;justify-content:center;'
            + 'background:transparent;border:0;border-radius:50%;cursor:pointer;'
            + '-webkit-tap-highlight-color:transparent;}'
            + '.pl-mui-btn:hover{background-color:rgba(127,127,127,.2);}'
            + '.pl-mui-btn:focus-visible{outline:2px solid currentColor;outline-offset:2px;}';
        var style = document.createElement('style');
        style.id = 'privateLibrariesStyles';
        style.textContent = css;
        document.head.appendChild(style);
    }

    function el(tag, cls, html) {
        var e = document.createElement(tag);
        if (cls) { e.className = cls; }
        if (html !== undefined) { e.innerHTML = html; }
        return e;
    }

    function closeDialog() {
        var o = document.getElementById(OVERLAY_ID);
        if (o) { o.remove(); }
        if (pendingReload) {
            pendingReload = false;
            window.location.reload();
        }
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function renderItemRow(item, actionLabel, actionClass, onAction) {
        var row = el('div', 'pl-row');
        var info = el('div', 'pl-grow');
        info.appendChild(el('div', 'pl-title', escapeHtml(item.Name || '(untitled)')));
        info.appendChild(el('div', 'pl-sub', escapeHtml((item.Type || '') + (item.Year ? ' · ' + item.Year : ''))));
        row.appendChild(info);
        var btn = el('button', 'pl-btn ' + actionClass, actionLabel);
        btn.addEventListener('click', function () { onAction(btn); });
        row.appendChild(btn);
        return row;
    }

    function openDialog() {
        injectStyles();
        var existing = document.getElementById(OVERLAY_ID);
        if (existing) { existing.remove(); }
        pendingReload = false;

        var overlay = el('div');
        overlay.id = OVERLAY_ID;
        overlay.addEventListener('click', function (e) { if (e.target === overlay) { closeDialog(); } });

        var dialog = el('div', 'pl-dialog');
        overlay.appendChild(dialog);

        var close = el('button', 'pl-close', '×');
        close.addEventListener('click', closeDialog);
        dialog.appendChild(close);

        dialog.appendChild(el('h2', null, 'My Private Library'));
        dialog.appendChild(el('div', 'pl-muted', 'Off (default): you see your whole library. Turn it on to limit yourself to only the titles you select or request.'));

        var errorBox = el('div', 'pl-error');
        dialog.appendChild(errorBox);
        function showError(msg) {
            errorBox.textContent = msg;
            errorBox.style.display = 'block';
        }
        function clearError() { errorBox.style.display = 'none'; }
        function fail(context, err) {
            var status = (err && (err.status || (err.response && err.response.status))) || '';
            // eslint-disable-next-line no-console
            console.error('[PrivateLibraries] ' + context + ' failed', err);

            function render(detail) {
                var msg = context + ' failed' + (status ? ' (HTTP ' + status + ')' : '');
                msg += detail ? ': ' + detail : '. See browser console / server log.';
                showError(msg);
            }

            // The controller returns the real exception message in the 500 body as
            // { "error": "..." }. Dig it out of whatever shape ApiClient.ajax rejected with
            // (jqXHR-style responseJSON/responseText, or a fetch Response we must read async).
            function extract(body) {
                if (!body) { return ''; }
                if (typeof body === 'string') {
                    try { return (JSON.parse(body) || {}).error || body; } catch (e) { return body; }
                }
                return body.error || body.message || '';
            }

            var immediate = extract(err && (err.responseJSON || err.responseText));
            if (immediate) { render(immediate); return; }

            if (err && typeof err.text === 'function') {
                err.text().then(function (t) { render(extract(t)); }).catch(function () { render(''); });
                return;
            }

            render('');
        }

        // Restriction toggle.
        var toggleWrap = el('div', 'pl-toggle');
        var sw = el('label', 'pl-switch');
        var cb = el('input');
        cb.type = 'checkbox';
        var slider = el('span', 'pl-slider');
        sw.appendChild(cb);
        sw.appendChild(slider);
        toggleWrap.appendChild(sw);
        toggleWrap.appendChild(el('div', 'pl-grow', 'Restrict my library to selected &amp; requested titles'));
        dialog.appendChild(toggleWrap);

        cb.addEventListener('change', function () {
            clearError();
            cb.disabled = true;
            apiSend('POST', 'Me/Restriction', { Enabled: cb.checked }).then(function () {
                cb.disabled = false;
                pendingReload = true;
            }).catch(function (err) {
                cb.disabled = false;
                cb.checked = !cb.checked;
                fail('Updating restriction', err);
            });
        });

        // Search section.
        dialog.appendChild(el('h3', null, 'Add titles'));
        var search = el('input', 'pl-input');
        search.type = 'search';
        search.placeholder = 'Type to search the library…';
        dialog.appendChild(search);
        var results = el('div');
        dialog.appendChild(results);

        // Current grants section.
        dialog.appendChild(el('h3', null, 'My titles'));
        var grants = el('div');
        dialog.appendChild(grants);

        document.body.appendChild(overlay);

        // Load current restriction state.
        apiGet('Me').then(function (me) {
            cb.checked = !!me.RestrictionEnabled;
        }).catch(function (err) { fail('Loading status', err); });

        function loadGrants() {
            grants.innerHTML = '';
            apiGet('Me/Grants').then(function (items) {
                if (!items || !items.length) {
                    grants.appendChild(el('div', 'pl-muted', 'Nothing added yet.'));
                    return;
                }
                items.forEach(function (item) {
                    grants.appendChild(renderItemRow(item, 'Remove', 'danger', function (btn) {
                        clearError();
                        btn.disabled = true;
                        apiSend('DELETE', 'Me/Grants/' + item.ItemId).then(function () {
                            pendingReload = true;
                            loadGrants();
                        }).catch(function (err) { btn.disabled = false; fail('Removing title', err); });
                    }));
                });
            }).catch(function (err) { fail('Loading your titles', err); });
        }

        var searchTimer;
        search.addEventListener('input', function () {
            clearTimeout(searchTimer);
            var q = search.value.trim();
            searchTimer = setTimeout(function () {
                if (!q) { results.innerHTML = ''; return; }
                clearError();
                apiGet('Search?query=' + encodeURIComponent(q)).then(function (items) {
                    results.innerHTML = '';
                    if (!items || !items.length) { results.appendChild(el('div', 'pl-muted', 'No matches.')); return; }
                    items.forEach(function (item) {
                        results.appendChild(renderItemRow(item, 'Add', 'secondary', function (btn) {
                            clearError();
                            btn.disabled = true;
                            apiSend('POST', 'Me/Grants', { ItemId: item.ItemId }).then(function () {
                                btn.textContent = 'Added';
                                pendingReload = true;
                                loadGrants();
                            }).catch(function (err) { btn.disabled = false; fail('Adding title', err); });
                        }));
                    });
                }).catch(function (err) { fail('Searching', err); });
            }, 300);
        });

        loadGrants();
    }

    function log(msg) {
        // eslint-disable-next-line no-console
        console.debug('[PrivateLibraries] ' + msg);
    }

    // .headerRight is always present in the DOM (scripts/libraryMenu.js renders it
    // unconditionally into .skinHeader), even on Jellyfin 12's default "modern" layout,
    // where RootAppRouter wraps it in a display:none ancestor and renders the MUI
    // toolbar instead. A plain querySelector null-check can't tell the two situations
    // apart, so this also confirms the header actually has layout (mirrors jQuery's
    // :visible test) before treating it as the real Jellyfin 10.11-style header.
    function isRendered(el) {
        return !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
    }

    // Jellyfin 10.11 (and Jellyfin 12 in "legacy" layout mode): insert into
    // .headerRight as a native icon button.
    function tryInjectLegacyHeader() {
        var header = document.querySelector('.headerRight');
        if (!header || !isRendered(header)) { return false; }
        var btn = document.createElement('button');
        btn.id = BTN_ID;
        btn.type = 'button';
        btn.className = 'headerButton headerButtonRight paper-icon-button-light';
        btn.title = 'My Private Library';
        btn.setAttribute('aria-label', 'My Private Library');
        btn.innerHTML = '<span class="material-icons" aria-hidden="true">video_library</span>';
        btn.addEventListener('click', openDialog);
        header.insertBefore(btn, header.firstChild);
        log('injected into .headerRight (Jellyfin 10.11)');
        return true;
    }

    // Jellyfin 12: the toolbar is owned by React, so inserting into it
    // directly causes React's reconciler to remove our node on the next
    // render. Instead we place the button on document.body and position it
    // dynamically relative to the user-menu button using getBoundingClientRect,
    // giving natural visual placement without fighting React.
    var _muiResizeObserver = null;

    // The user-avatar menu button (and therefore the whole toolbar it lives in) is
    // absent on pages that don't show the library-oriented toolbar at all - the video
    // OSD (AppToolbar returns null there) and "public" paths like login/select-server
    // (isUserMenuAvailable=false). The dashboard/admin app (including this plugin's
    // own config page) reuses the exact same toolbar and avatar button, but sets this
    // class on <body> for its own CSS scoping - reused here to hide our button there
    // too, matching where SyncPlay/RemotePlay/Search are also never shown.
    function isMuiToolbarButtonAllowed() {
        return !!document.querySelector('[aria-controls="app-user-menu"]')
            && !document.body.classList.contains('dashboardDocument');
    }

    function removeMuiButton() {
        var btn = document.getElementById(BTN_ID);
        if (btn) { btn.remove(); }
        if (_muiResizeObserver) {
            _muiResizeObserver.disconnect();
            _muiResizeObserver = null;
        }
    }

    function positionMuiButton(btn) {
        var ref = document.querySelector('[aria-controls="app-user-menu"]');
        if (!ref) { return; }

        // Our <button> is appended to document.body, not into the toolbar itself, so
        // it never picks up the toolbar's (theme-dependent) icon colour by normal CSS
        // inheritance. Copy it from a real toolbar icon button on every reposition so
        // it keeps matching if the user switches between light/dark theme at runtime.
        btn.style.color = getComputedStyle(ref).color;

        var avatarRect = ref.getBoundingClientRect();

        // The avatar button sits in its own flex-shrink Box, immediately preceded by a
        // sibling Box (flexGrow:1, justifyContent:flex-end) holding the page's toolbar
        // actions - SyncPlay / RemotePlay(Cast) / Search, whichever the current page
        // renders. That sibling Box always exists (even with no children), but it
        // stretches to fill the toolbar's leftover space, so *its own* left edge is
        // usually far from the avatar - only its rendered children are actually packed
        // against the avatar. Anchor to the leftmost visible one of those instead of
        // hardcoding a fixed offset from the avatar, otherwise our button lands on top
        // of whichever action button the current page happens to render closest to the
        // avatar (e.g. Search).
        var leftAnchorRect = avatarRect;
        var actionsGroup = ref.parentElement && ref.parentElement.previousElementSibling;
        if (actionsGroup) {
            for (var i = 0; i < actionsGroup.children.length; i++) {
                var childRect = actionsGroup.children[i].getBoundingClientRect();
                if (childRect.width || childRect.height) {
                    leftAnchorRect = childRect;
                    break;
                }
            }
        }

        // Centre vertically on the avatar; place immediately to the left of whatever
        // was found above.
        btn.style.top = Math.round(avatarRect.top + (avatarRect.height - 40) / 2) + 'px';
        btn.style.left = Math.round(leftAnchorRect.left - 44) + 'px';
    }

    function tryInjectMuiToolbar() {
        // Only activate when the MUI toolbar's user menu is present (confirms we are
        // on v12) and we're not on a page that shouldn't show it (see above).
        if (!isMuiToolbarButtonAllowed()) { return false; }
        var userMenuBtn = document.querySelector('[aria-controls="app-user-menu"]');

        var btn = document.createElement('button');
        btn.id = BTN_ID;
        btn.type = 'button';
        btn.title = 'My Private Library';
        btn.setAttribute('aria-label', 'My Private Library');
        btn.setAttribute('tabindex', '0');
        // pl-mui-btn (defined in injectStyles) resets the browser's default <button>
        // chrome (opaque face, border) that otherwise made this look like a solid
        // white box sitting on top of the header instead of a real icon button. The
        // Mui* classes are kept only for identification/consistency with real MUI
        // markup, not because they supply any styling on their own.
        btn.className = 'MuiButtonBase-root MuiIconButton-root MuiIconButton-sizeLarge pl-mui-btn';
        btn.style.zIndex = '1200';
        btn.innerHTML = '<svg class="MuiSvgIcon-root" xmlns="http://www.w3.org/2000/svg" '
            + 'focusable="false" aria-hidden="true" viewBox="0 0 24 24" fill="currentColor">'
            + '<path d="M4 6H2v14c0 1.1.9 2 2 2h14v-2H4V6zm16-4H8c-1.1 0-2 .9-2 2v12'
            + 'c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-8 12.5v-9l6 4.5-6 4.5z"/>'
            + '</svg>';
        btn.addEventListener('click', openDialog);
        document.body.appendChild(btn);

        // Set initial position and keep it updated when the toolbar resizes
        // (e.g. window resize, drawer open/close changing toolbar width).
        positionMuiButton(btn);
        if (typeof ResizeObserver !== 'undefined') {
            var toolbar = userMenuBtn.closest('.MuiToolbar-root');
            if (toolbar) {
                if (_muiResizeObserver) { _muiResizeObserver.disconnect(); }
                _muiResizeObserver = new ResizeObserver(function () { positionMuiButton(btn); });
                _muiResizeObserver.observe(toolbar);
            }
        }
        window.addEventListener('resize', function () { positionMuiButton(btn); });

        log('injected as MUI-positioned fixed button (Jellyfin 12)');
        return true;
    }

    function ensureButton() {
        var existing = document.getElementById(BTN_ID);
        if (existing) {
            // The v10.11 button lives inside .headerRight, so it's automatically
            // hidden along with it by CSS on pages that shouldn't show it (see
            // tryInjectLegacyHeader). The v12 button lives directly on document.body
            // though, so nothing else will ever remove or hide it - do that here
            // whenever it no longer belongs on the current page (dashboard/admin,
            // video OSD, public paths), and let ensureButton() recreate it via
            // tryInjectMuiToolbar() once we're back somewhere it belongs.
            if (existing.parentElement === document.body && !isMuiToolbarButtonAllowed()) {
                removeMuiButton();
                return;
            }
            // Otherwise it may just need repositioning if the toolbar shifted (e.g.
            // after a React re-render changed the user-menu button's position).
            positionMuiButton(existing);
            return;
        }
        // Try v10.11 DOM first; fall back to MUI toolbar for v12.
        if (!tryInjectLegacyHeader()) {
            tryInjectMuiToolbar();
        }
    }

    // The header re-renders on navigation. For v10.11 the button lives inside
    // the header so the observer re-inserts it when React or routing wipes it.
    // For v12 the button is on document.body and survives re-renders; the observer
    // re-positions it whenever the toolbar DOM changes, and removes it when
    // navigating to a page it doesn't belong on (see isMuiToolbarButtonAllowed).
    var observer = new MutationObserver(function () { ensureButton(); });
    function start() {
        log('starting');
        // Needed up front (not just lazily on first dialog open) so the .pl-mui-btn
        // rules below exist as soon as the Jellyfin 12 button is first injected.
        injectStyles();
        ensureButton();
        // attributes/attributeFilter on the class attribute is needed in addition to
        // childList/subtree: navigating into/out of the dashboard app only *sometimes*
        // also mutates unrelated content in the same tick (childList changes
        // elsewhere in the page). Watching the dashboardDocument class directly makes
        // hiding/showing the v12 button on that transition deterministic instead of
        // relying on an incidental mutation to also happen to occur.
        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['class']
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
