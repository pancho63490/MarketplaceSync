// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// Formularios lentos (fotos, etc.): <form data-busy="Texto…"> desactiva el botón y muestra el texto mientras trabaja.
document.addEventListener('submit', function (e) {
    var form = e.target;
    if (!(form instanceof HTMLFormElement) || !form.dataset.busy || e.defaultPrevented) return;
    var button = e.submitter || form.querySelector('button[type=submit]');
    if (!button) return;
    setTimeout(function () { button.disabled = true; button.textContent = form.dataset.busy; }, 0);
});

// ── Tema claro / oscuro (se guarda en este navegador) ──
(function () {
    function apply(t) {
        document.documentElement.setAttribute('data-bs-theme', t);
        var meta = document.querySelector('meta[name="theme-color"]');
        if (meta) meta.setAttribute('content', t === 'dark' ? '#0c111b' : '#f6f7f9');
        document.dispatchEvent(new CustomEvent('ms:theme', { detail: t }));
    }
    apply(document.documentElement.getAttribute('data-bs-theme') || 'light');
    document.addEventListener('click', function (e) {
        var b = e.target.closest('[data-theme-toggle]');
        if (!b) return;
        var t = document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
        try { localStorage.setItem('ms-theme', t); } catch (err) { }
        apply(t);
    });
})();

// ── Avisos flotantes (Notyf) ──
window.msToast = (function () {
    if (typeof Notyf === 'undefined') return function () { };
    var notyf = new Notyf({
        duration: 5000,
        ripple: false,
        dismissible: true,
        position: { x: 'right', y: 'top' },
        types: [
            { type: 'success', className: 'ms-toast ms-toast-ok', icon: false },
            { type: 'error', className: 'ms-toast ms-toast-bad', icon: false, duration: 9000 },
            { type: 'warning', className: 'ms-toast ms-toast-warn', icon: false, duration: 8000 },
            { type: 'info', className: 'ms-toast ms-toast-info', icon: false }
        ]
    });
    return function (type, message) { notyf.open({ type: type, message: message }); };
})();
document.querySelectorAll('script[data-toast]').forEach(function (s) {
    try { JSON.parse(s.textContent).forEach(function (t) { window.msToast(t.type, t.message); }); } catch (e) { }
});

// ── Buscador Ctrl+K ──
(function () {
    var dialog = document.getElementById('cmdk');
    var dataEl = document.getElementById('cmdkData');
    if (!dialog || !dataEl || typeof dialog.showModal !== 'function') return;
    var data = JSON.parse(dataEl.textContent);
    var input = document.getElementById('cmdkInput');
    var list = document.getElementById('cmdkList');
    var items = [], active = 0, timer = null, seq = 0, remote = { products: [], orders: [] };

    function norm(s) { return (s || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, ''); }
    function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : String(s); return d.innerHTML; }
    function icon(name) { return '<svg class="icon" aria-hidden="true"><use href="/img/icons.svg?v=6#i-' + name + '"></use></svg>'; }

    function render() {
        var q = norm(input.value.trim());
        var groups = [];
        var pages = data.pages.filter(function (p) { return !q || norm(p.label + ' ' + p.g).indexOf(q) >= 0; });
        var actions = data.actions.filter(function (p) { return !q || norm(p.label).indexOf(q) >= 0; });
        if (q && remote.products.length) groups.push(['Productos', remote.products.map(function (p) { return { label: p.label, sub: p.sub, url: p.url, image: p.image, icon: 'package' }; })]);
        if (q && remote.orders.length) groups.push(['Pedidos', remote.orders.map(function (p) { return { label: p.label, sub: p.sub, url: p.url, icon: 'receipt' }; })]);
        if (actions.length) groups.push(['Acciones', actions]);
        if (pages.length) groups.push(['Pantallas', pages.slice(0, q ? 8 : 30)]);
        items = [];
        var html = '';
        groups.forEach(function (g) {
            html += '<div class="cmdk-group" role="presentation">' + esc(g[0]) + '</div>';
            g[1].forEach(function (it) {
                var i = items.length; items.push(it);
                var media = it.image ? '<span class="cmdk-thumb"><img src="' + esc(it.image) + '" alt="" loading="lazy" referrerpolicy="no-referrer" onerror="this.remove()"></span>' : '<span class="cmdk-ico">' + icon(it.icon || 'arrow-right') + '</span>';
                html += '<a class="cmdk-item" role="option" id="cmdk-' + i + '" href="' + esc(it.url) + '" data-i="' + i + '">' + media +
                    '<span class="cmdk-text"><span class="cmdk-label">' + esc(it.label) + '</span>' + (it.sub ? '<span class="cmdk-sub">' + esc(it.sub) + '</span>' : '') + '</span>' +
                    '<span class="cmdk-go" aria-hidden="true">' + icon('corner-down-left') + '</span></a>';
            });
        });
        if (!items.length) html = '<div class="cmdk-empty">' + (q.length >= 2 ? 'No encontré nada con «' + esc(input.value.trim()) + '». Prueba con el número de pedido, el comprador o el número de parte.' : 'Escribe para buscar.') + '</div>';
        list.innerHTML = html;
        active = Math.min(active, Math.max(0, items.length - 1));
        mark();
    }
    function mark() {
        list.querySelectorAll('.cmdk-item').forEach(function (a) {
            var on = Number(a.dataset.i) === active;
            a.classList.toggle('is-active', on);
            a.setAttribute('aria-selected', on ? 'true' : 'false');
            if (on) { input.setAttribute('aria-activedescendant', a.id); a.scrollIntoView({ block: 'nearest' }); }
        });
    }
    function search() {
        var q = input.value.trim();
        clearTimeout(timer);
        if (q.length < 2) { remote = { products: [], orders: [] }; render(); return; }
        timer = setTimeout(function () {
            var my = ++seq;
            list.classList.add('is-loading');
            fetch(data.search + '?q=' + encodeURIComponent(q), { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
                .then(function (r) { return r.ok ? r.json() : { products: [], orders: [] }; })
                .then(function (d) { if (my !== seq) return; remote = d; active = 0; render(); })
                .catch(function () { })
                .finally(function () { if (my === seq) list.classList.remove('is-loading'); });
        }, 160);
    }
    function open() {
        if (dialog.open) return;
        input.value = ''; remote = { products: [], orders: [] }; active = 0; render();
        dialog.showModal(); input.focus();
    }
    function go(i) { var it = items[i]; if (it) { dialog.close(); window.location.href = it.url; } }

    document.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) { e.preventDefault(); dialog.open ? dialog.close() : open(); }
        else if (e.key === '/' && !dialog.open && !/^(input|textarea|select)$/i.test((e.target.tagName || '')) && !e.target.isContentEditable) { e.preventDefault(); open(); }
    });
    document.addEventListener('click', function (e) { if (e.target.closest('[data-cmdk-open]')) { e.preventDefault(); open(); } });
    input.addEventListener('input', function () { active = 0; render(); search(); });
    input.addEventListener('keydown', function (e) {
        if (e.key === 'ArrowDown') { e.preventDefault(); active = Math.min(items.length - 1, active + 1); mark(); }
        else if (e.key === 'ArrowUp') { e.preventDefault(); active = Math.max(0, active - 1); mark(); }
        else if (e.key === 'Enter') { e.preventDefault(); go(active); }
    });
    list.addEventListener('mousemove', function (e) { var a = e.target.closest('.cmdk-item'); if (a && Number(a.dataset.i) !== active) { active = Number(a.dataset.i); mark(); } });
    dialog.addEventListener('click', function (e) { if (e.target === dialog) dialog.close(); });
})();
