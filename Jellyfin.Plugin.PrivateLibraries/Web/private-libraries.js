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

    function getApiClient() {
        return window.ApiClient || (window.connectionManager && window.connectionManager.currentApiClient && window.connectionManager.currentApiClient());
    }

    function api(path, opts) {
        var client = getApiClient();
        opts = opts || {};
        opts.headers = Object.assign(
            { 'X-Emby-Token': client.accessToken(), 'Content-Type': 'application/json' },
            opts.headers || {});
        return fetch(client.getUrl('PrivateLibraries/' + path), opts);
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
            + '.pl-input{width:100%;box-sizing:border-box;padding:10px 12px;border-radius:6px;border:1px solid #2b333c;background:#181d22;color:#eee;font-size:.95em;}'
            + '.pl-toggle{display:flex;align-items:center;gap:12px;background:#181d22;border-radius:8px;padding:12px 14px;margin-bottom:6px;}'
            + '.pl-switch{position:relative;width:46px;height:26px;flex:0 0 auto;}'
            + '.pl-switch input{opacity:0;width:0;height:0;}'
            + '.pl-slider{position:absolute;inset:0;background:#555;border-radius:26px;transition:.2s;cursor:pointer;}'
            + '.pl-slider:before{content:"";position:absolute;height:20px;width:20px;left:3px;bottom:3px;background:#fff;border-radius:50%;transition:.2s;}'
            + '.pl-switch input:checked + .pl-slider{background:#00a4dc;}'
            + '.pl-switch input:checked + .pl-slider:before{transform:translateX(20px);}'
            + '.pl-close{float:right;background:none;border:0;color:#aaa;font-size:1.6em;cursor:pointer;line-height:1;}'
            + '.pl-muted{opacity:.6;font-size:.9em;padding:6px 0;}';
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

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function openDialog() {
        injectStyles();
        closeDialog();

        var overlay = el('div');
        overlay.id = OVERLAY_ID;
        overlay.addEventListener('click', function (e) { if (e.target === overlay) { closeDialog(); } });

        var dialog = el('div', 'pl-dialog');
        overlay.appendChild(dialog);

        var close = el('button', 'pl-close', '×');
        close.addEventListener('click', closeDialog);
        dialog.appendChild(close);

        dialog.appendChild(el('h2', null, 'My Private Library'));
        dialog.appendChild(el('div', 'pl-muted', 'Choose which titles appear in your libraries. Turn the restriction off to see everything.'));

        // Restriction toggle.
        var toggleWrap = el('div', 'pl-toggle');
        var sw = el('label', 'pl-switch');
        var cb = el('input');
        cb.type = 'checkbox';
        var slider = el('span', 'pl-slider');
        sw.appendChild(cb);
        sw.appendChild(slider);
        toggleWrap.appendChild(sw);
        var toggleLabel = el('div', 'pl-grow', 'Restrict my library to selected & requested titles');
        toggleWrap.appendChild(toggleLabel);
        dialog.appendChild(toggleWrap);

        cb.addEventListener('change', function () {
            cb.disabled = true;
            api('Me/Restriction', { method: 'POST', body: JSON.stringify({ Enabled: cb.checked }) })
                .then(function () { cb.disabled = false; })
                .catch(function () { cb.disabled = false; cb.checked = !cb.checked; });
        });

        // Search section.
        dialog.appendChild(el('h3', null, 'Add titles'));
        var search = el('input', 'pl-input');
        search.type = 'search';
        search.placeholder = 'Search the library…';
        dialog.appendChild(search);
        var results = el('div');
        dialog.appendChild(results);

        // Current grants section.
        dialog.appendChild(el('h3', null, 'My titles'));
        var grants = el('div');
        dialog.appendChild(grants);

        document.body.appendChild(overlay);

        // Load current state.
        api('Me').then(function (r) { return r.json(); }).then(function (me) {
            cb.checked = !!me.RestrictionEnabled;
        }).catch(function () {});

        function loadGrants() {
            grants.innerHTML = '';
            api('Me/Grants').then(function (r) { return r.json(); }).then(function (items) {
                if (!items.length) {
                    grants.appendChild(el('div', 'pl-muted', 'Nothing added yet.'));
                    return;
                }
                items.forEach(function (item) {
                    grants.appendChild(renderItemRow(item, 'Remove', 'danger', function (btn) {
                        btn.disabled = true;
                        api('Me/Grants/' + item.ItemId, { method: 'DELETE' }).then(function () {
                            loadGrants();
                        }).catch(function () { btn.disabled = false; });
                    }));
                });
            }).catch(function () {});
        }

        var searchTimer;
        search.addEventListener('input', function () {
            clearTimeout(searchTimer);
            var q = search.value.trim();
            searchTimer = setTimeout(function () {
                if (!q) { results.innerHTML = ''; return; }
                api('Search?query=' + encodeURIComponent(q)).then(function (r) { return r.json(); }).then(function (items) {
                    results.innerHTML = '';
                    if (!items.length) { results.appendChild(el('div', 'pl-muted', 'No matches.')); return; }
                    items.forEach(function (item) {
                        results.appendChild(renderItemRow(item, 'Add', 'secondary', function (btn) {
                            btn.disabled = true;
                            api('Me/Grants', { method: 'POST', body: JSON.stringify({ ItemId: item.ItemId }) }).then(function () {
                                btn.textContent = 'Added';
                                loadGrants();
                            }).catch(function () { btn.disabled = false; });
                        }));
                    });
                }).catch(function () {});
            }, 300);
        });

        loadGrants();
    }

    function makeButton() {
        var btn = document.createElement('button');
        btn.id = BTN_ID;
        btn.type = 'button';
        btn.className = 'headerButton headerButtonRight paper-icon-button-light';
        btn.title = 'My Private Library';
        btn.setAttribute('aria-label', 'My Private Library');
        btn.innerHTML = '<span class="material-icons" aria-hidden="true">video_library</span>';
        btn.addEventListener('click', openDialog);
        return btn;
    }

    function ensureButton() {
        if (document.getElementById(BTN_ID)) {
            return;
        }
        var header = document.querySelector('.headerRight');
        if (!header) {
            return;
        }
        header.insertBefore(makeButton(), header.firstChild);
    }

    // The header is re-rendered on navigation, so keep re-checking.
    var observer = new MutationObserver(function () { ensureButton(); });
    function start() {
        ensureButton();
        observer.observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
