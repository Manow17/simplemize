(function () {
  'use strict';

  var data = window.SIMPLEMIZE || { version: '1.0.0', tweaks: [] };
  var tweaks = data.tweaks;
  var SECTIONS = ['Performance', 'Hardware', 'Debloat', 'Processes', 'Cleanup', 'Extras'];
  var state = { section: 'Performance', level: 'all', q: '' };

  // ---- tiny DOM helper -------------------------------------------------------------
  function h(tag, attrs, kids) {
    var el = document.createElement(tag);
    if (attrs) for (var k in attrs) {
      if (k === 'class') el.className = attrs[k];
      else if (k === 'text') el.textContent = attrs[k];
      else el.setAttribute(k, attrs[k]);
    }
    (kids || []).forEach(function (c) { if (c != null) el.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
    return el;
  }

  // ---- theme ------------------------------------------------------------------------
  var root = document.documentElement;
  function isDark() {
    var t = root.getAttribute('data-theme');
    if (t) return t === 'dark';
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
  }
  function syncShots() {
    var dark = isDark();
    document.querySelectorAll('img[data-light]').forEach(function (img) {
      var want = dark ? img.getAttribute('data-dark') : img.getAttribute('data-light');
      if (want && img.getAttribute('src') !== want) img.setAttribute('src', want);
    });
  }
  document.querySelector('.theme').addEventListener('click', function () {
    var next = isDark() ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    try { localStorage.setItem('simplemize-theme', next); } catch (e) {}
    syncShots();
  });
  if (window.matchMedia) {
    var mq = window.matchMedia('(prefers-color-scheme: dark)');
    if (mq.addEventListener) mq.addEventListener('change', syncShots);
  }
  syncShots();

  // ---- copy buttons -------------------------------------------------------------------
  document.querySelectorAll('[data-copy]').forEach(function (b) {
    b.addEventListener('click', function () {
      var text = document.getElementById(b.getAttribute('data-copy')).textContent;
      var done = function () { b.textContent = 'Copied'; setTimeout(function () { b.textContent = 'Copy'; }, 1600); };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(done, function () { select(); });
      } else select();
      function select() {
        var r = document.createRange(); r.selectNodeContents(document.getElementById(b.getAttribute('data-copy')));
        var s = window.getSelection(); s.removeAllRanges(); s.addRange(r);
        b.textContent = 'Press Ctrl+C';
      }
    });
  });

  // ---- figures ----------------------------------------------------------------------
  var isApp = function (t) { return t.group === 'Preinstalled apps'; };
  var counts = {
    tweaks: tweaks.filter(function (t) { return !isApp(t); }).length,
    revertible: tweaks.filter(function (t) { return t.revertible; }).length,
    apps: tweaks.filter(isApp).length
  };
  document.querySelectorAll('[data-count]').forEach(function (el) { el.textContent = counts[el.getAttribute('data-count')]; });
  document.querySelectorAll('[data-version]').forEach(function (el) { el.textContent = data.version; });

  // ---- browser ----------------------------------------------------------------------
  var tabs = document.querySelector('.tabs');
  var list = document.querySelector('.browser .list');
  var search = document.querySelector('.search input');

  SECTIONS.forEach(function (s) {
    var n = tweaks.filter(function (t) { return t.section === s; }).length;
    var b = h('button', { type: 'button', role: 'tab', 'aria-selected': s === state.section ? 'true' : 'false', 'data-section': s },
      [s, h('small', { text: String(n) })]);
    b.addEventListener('click', function () {
      state.section = s;
      tabs.querySelectorAll('button').forEach(function (x) { x.setAttribute('aria-selected', x === b ? 'true' : 'false'); });
      render();
    });
    tabs.appendChild(b);
  });

  document.querySelectorAll('.seg button').forEach(function (b) {
    b.addEventListener('click', function () {
      state.level = b.getAttribute('data-level');
      document.querySelectorAll('.seg button').forEach(function (x) { x.classList.toggle('on', x === b); });
      render();
    });
  });

  search.addEventListener('input', function () { state.q = search.value.trim().toLowerCase(); render(); });

  function matches(t) {
    if (state.level !== 'all' && t.level !== state.level) return false;
    if (!state.q) return t.section === state.section;
    var hay = [t.title, t.summary, t.gain, t.tradeoff, t.group, (t.affects || []).join(' ')]
      .concat((t.changes || []).map(function (c) { return c.where + ' ' + c.what; })).join(' ').toLowerCase();
    return hay.indexOf(state.q) >= 0;
  }

  function tag(level) { return h('span', { class: 'tag' + (level === 'heavy' ? ' heavy' : ''), text: level.toUpperCase() }); }

  function kv(label, content) {
    return h('div', { class: 'kv' }, [h('span', { class: 'label', text: label }), content]);
  }

  function tweakEl(t) {
    var meta = h('div', { class: 'tw-meta' }, [
      t.affects && t.affects.length ? h('span', { text: 'affects ' + t.affects.join(' · ') }) : null,
      t.after && t.after.length ? h('span', { text: t.after[0] === 'restart' ? 'restart needed' : t.after[0] === 'sign out' ? 'sign out to finish' : 'restarts Explorer' }) : null,
      t.security ? h('span', { class: 'warn-sec', text: 'lowers a security protection' }) : null,
      h('span', { class: 'open' })
    ]);
    var right = h('div', { class: 'tw-right' }, [tag(t.level), t.requires ? h('span', { class: 'req', text: t.requires }) : null]);
    var summary = h('summary', null, [
      h('span', { class: 'tw-title', text: t.title }), right,
      h('p', { class: 'tw-sum', text: t.summary }), meta
    ]);

    var changes = h('div', null, (t.changes || []).map(function (c) {
      return h('p', { class: 'chg' }, [h('span', { text: c.where }), c.what]);
    }));
    var undo = h('p', null, [
      (!t.revertible && !t.oneShot) ? h('span', { class: 'bad', text: 'Not undoable by Simplemize. ' }) : null,
      (!t.revertible && !t.oneShot) ? t.undo.replace(/^Simplemize can't[^.]*\.\s*/, '') : t.undo
    ]);
    var body = h('div', { class: 'tw-body' }, [
      t.gain ? kv('You get', h('p', { text: t.gain })) : null,
      t.tradeoff ? kv('Trade-off', h('p', { text: t.tradeoff })) : null,
      kv('Changes', changes),
      kv('Undo', undo)
    ]);
    return h('details', { class: 'tw' }, [summary, body]);
  }

  function appsTable(apps) {
    var rows = apps.map(function (t) {
      return h('tr', null, [
        h('td', { text: t.title }),
        h('td', { text: t.summary }),
        h('td', { text: t.presets.indexOf('stripped') >= 0 ? 'in Stripped preset' : 'your call' })
      ]);
    });
    return h('table', { class: 'apps' }, [h('tbody', null, rows)]);
  }

  function render() {
    list.textContent = '';
    var shown = tweaks.filter(matches);
    if (!shown.length) {
      list.appendChild(h('p', { class: 'empty', text: 'Nothing matches that. Try another word, or set the filter to All.' }));
      return;
    }
    var groups = [];
    var byName = {};
    shown.forEach(function (t) {
      var key = (state.q ? t.section + ' · ' : '') + t.group;
      if (!byName[key]) { byName[key] = { name: key, items: [] }; groups.push(byName[key]); }
      byName[key].items.push(t);
    });
    groups.forEach(function (g) {
      var apps = g.items.every(isApp);
      var head = h('div', { class: 'group-h' }, [h('span', { text: g.name }), h('span', { text: String(g.items.length) })]);
      var box = h('div', { class: 'group' }, [head]);
      if (apps) {
        box.appendChild(h('p', { class: 'tw-sum', style: 'margin:14px 0 4px', text:
          'Each app is its own checkbox in Simplemize, and only apps that are actually installed are listed. ' +
          'All of these are heavy: removed apps are uninstalled for every account and can only come back through the Microsoft Store (free).' }));
        box.appendChild(appsTable(g.items));
      } else {
        g.items.forEach(function (t) { box.appendChild(tweakEl(t)); });
      }
      list.appendChild(box);
    });
  }

  render();
})();
