// Интерактивность HTML-макета: рамки устройств, значки, островок со щитом, переходы, тумблеры, сегменты, флажки,
// диалоги, листы, меню, вкладки, тосты и состояния защиты. Без библиотек и без сети: страницы открываются двойным
// щелчком (file://). Экран один и тот же на всех устройствах — меняются только рамка ОС, шрифт и поля.
(function () {
  "use strict";

  var BOARDS = [
    { file: "01-main.html", n: 1, title: "Главная", group: "Основные экраны" },
    { file: "02-servers.html", n: 2, title: "Серверы", group: "Основные экраны" },
    { file: "03-import.html", n: 3, title: "Добавить профиль", group: "Основные экраны" },
    { file: "04-settings.html", n: 4, title: "Настройки", group: "Основные экраны" },
    { file: "05-protection.html", n: 5, title: "Защита и утечки", group: "Основные экраны" },
    { file: "06-advanced.html", n: 6, title: "Подключение и ядро", group: "Основные экраны" },
    { file: "07-diagnostics.html", n: 7, title: "Проверка и логи", group: "Основные экраны" },
    { file: "08-tray.html", n: 8, title: "Меню в трее + подменю", group: "Основные экраны" },
    { file: "09-states.html", n: 9, title: "Состояния главного экрана", group: "Основные экраны" },
    { file: "10-onboarding.html", n: 10, title: "Первый запуск", group: "Дополнительные экраны и сценарии" },
    { file: "11-server-card.html", n: 11, title: "Карточка сервера", group: "Дополнительные экраны и сценарии" },
    { file: "12-subscriptions.html", n: 12, title: "Профили и подписки", group: "Дополнительные экраны и сценарии" },
    { file: "13-routing.html", n: 13, title: "Маршрутизация", group: "Дополнительные экраны и сценарии" },
    { file: "14-backup.html", n: 14, title: "Резервная копия", group: "Дополнительные экраны и сценарии" },
    { file: "15-about.html", n: 15, title: "О программе и обновление", group: "Дополнительные экраны и сценарии" },
    { file: "16-errors.html", n: 16, title: "Ошибки", group: "Дополнительные экраны и сценарии" },
    { file: "17-main-variants.html", n: 17, title: "Главная: прокси и вход в Wi-Fi", group: "Дополнительные экраны и сценарии" },
    { file: "18-dialogs.html", n: 18, title: "Диалоги подтверждения", group: "Дополнительные экраны и сценарии" },
    { file: "19-notifications.html", n: 19, title: "Уведомления: какие бывают", group: "Дополнительные экраны и сценарии" },
    { file: "20-platforms.html", n: 20, title: "Окно на Windows, macOS, Linux", group: "Три ОС: Windows · macOS · Linux" },
    { file: "21-platform-specifics.html", n: 21, title: "Трей, строка меню и установка службы по ОС", group: "Три ОС: Windows · macOS · Linux" },
    { file: "22-notify-os.html", n: 22, title: "Уведомления на Windows, macOS, Linux", group: "Три ОС: Windows · macOS · Linux" },
    { file: "23-ds-foundations.html", n: 23, title: "Дизайн-система: основы", group: "Дизайн-система" },
    { file: "24-ds-components.html", n: 24, title: "Дизайн-система: компоненты", group: "Дизайн-система" },
    { file: "25-mobile-main.html", n: 25, title: "Мобильные: главный экран", group: "Мобильные: Android и iOS" },
    { file: "26-mobile-system.html", n: 26, title: "Android и iOS: системные окна и технологии", group: "Мобильные: Android и iOS" },
  ];
  var DEVICES = [
    { id: "windows", title: "Windows" }, { id: "macos", title: "macOS" }, { id: "linux", title: "Linux" },
    { id: "android", title: "Android" }, { id: "ios", title: "iOS" },
  ];
  // Экраны островка: «Главная», «Серверы», «Проверка», «Настройки» (IslandNav программы)
  var NAV = [
    { id: "home", file: "01-main.html", icon: "Nav.Home", title: "Главная" },
    { id: "servers", file: "02-servers.html", icon: "Nav.Servers", detail: "Nav.Servers.Detail", title: "Серверы" },
    null,
    { id: "check", file: "07-diagnostics.html", icon: "Nav.Check", detail: "Nav.Check.Detail", title: "Проверка" },
    { id: "settings", file: "04-settings.html", icon: "Nav.Settings", detail: "Nav.Settings.Detail", title: "Настройки" },
  ];
  // Подпись под щитом и знак в щите по состоянию (ProtectionViewModel.Label и Mark)
  var STATE = {
    connected: { label: "Защищено", mark: "Mark.Ok" },
    connecting: { label: "Подключение", mark: "Mark.Wait" },
    reconnecting: { label: "Подключение", mark: "Mark.Wait" },
    blocked: { label: "Блокировка", mark: "Mark.Alert" },
    strict: { label: "Блокировка", mark: "Mark.Alert" },
    error: { label: "Ошибка", mark: "Mark.Alert" },
    off: { label: "Выключено", mark: "Mark.Off" },
    proxy: { label: "Прокси", mark: "Mark.Alert" },
    paused: { label: "Пауза", mark: "Mark.Alert" },
  };

  var params = new URLSearchParams(location.search);
  var device = DEVICES.some(function (d) { return d.id === params.get("device"); }) ? params.get("device") : "windows";
  var thumb = params.get("thumb") === "1";
  var SVGNS = "http://www.w3.org/2000/svg";

  function icon(name, detail, stroke) {
    var svg = document.createElementNS(SVGNS, "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("class", "pg-icon");
    svg.setAttribute("aria-hidden", "true");
    [name, detail].forEach(function (key) {
      if (!key) { return; }
      var d = (window.PG_ICONS || {})[key];
      if (!d) { console.warn("Нет значка " + key); return; }
      var path = document.createElementNS(SVGNS, "path");
      path.setAttribute("d", d);
      path.setAttribute("stroke-width", stroke || "1.8");
      svg.appendChild(path);
    });
    return svg;
  }

  function renderIcons(root) {
    root.querySelectorAll("i[data-icon]").forEach(function (el) {
      var svg = icon(el.dataset.icon, el.dataset.detail, el.dataset.stroke);
      if (el.className) { svg.setAttribute("class", "pg-icon " + el.className); }
      if (el.dataset.size) { svg.style.width = svg.style.height = el.dataset.size + "px"; }
      if (el.dataset.role) { svg.dataset.role = el.dataset.role; }
      el.replaceWith(svg);
    });
  }

  function withDevice(file, dev) {
    return file + "?device=" + (dev || device);
  }

  // Форма островка — как IslandGeometry.Path программы: выступ под щит по центру
  function islandPath(width, side) {
    var cx = width / 2, right = width - side;
    function n(v) { return Math.round(v * 100) / 100; }
    return "M" + (side + 26) + " 70 H" + n(cx - 63.5) + " A18 18 0 0 0 " + n(cx - 46.18) + " 56.91 A48 48 0 0 1 " + n(cx + 46.18) +
      " 56.91 A18 18 0 0 0 " + n(cx + 63.5) + " 70 H" + n(right - 26) + " A26 26 0 0 1 " + n(right) + " 96 V112 A26 26 0 0 1 " +
      n(right - 26) + " 138 H" + (side + 26) + " A26 26 0 0 1 " + side + " 112 V96 A26 26 0 0 1 " + (side + 26) + " 70 Z";
  }

  function buildIsland(el, frameDevice) {
    var active = el.dataset.active || "";
    el.innerHTML = "";
    var svg = document.createElementNS(SVGNS, "svg");
    var path = document.createElementNS(SVGNS, "path");
    svg.appendChild(path);
    el.appendChild(svg);
    var nav = document.createElement("div");
    nav.className = "pg-island-nav";
    NAV.forEach(function (item) {
      if (!item) {
        var label = document.createElement("div");
        label.className = "pg-island-state";
        label.dataset.role = "state-label";
        nav.appendChild(label);
        return;
      }
      var b = document.createElement("button");
      b.className = "btn-nav" + (item.id === active ? " active" : "");
      b.dataset.nav = item.file;
      b.setAttribute("aria-label", item.title);
      b.appendChild(icon(item.icon, item.detail, "1.7"));
      var t = document.createElement("span");
      t.textContent = item.title;
      b.appendChild(t);
      nav.appendChild(b);
    });
    el.appendChild(nav);
    var wrap = document.createElement("div");
    wrap.className = "power-wrap";
    wrap.innerHTML = '<button class="power" data-power aria-label="Включить защиту"></button>' +
      '<svg class="power-spin" viewBox="0 0 80 80"><circle cx="40" cy="40" r="38" stroke-dasharray="66 300" transform="rotate(-90 40 40)"/></svg>';
    el.appendChild(wrap);
    var sideVar = frameDevice === "android" || frameDevice === "ios" ? 14 : 12;
    function resize() {
      var w = el.clientWidth || 440;
      svg.setAttribute("viewBox", "0 0 " + w + " 150");
      path.setAttribute("d", islandPath(w, sideVar));
    }
    resize();
    new ResizeObserver(resize).observe(el);
  }

  // Состояние защиты: щит, подпись под ним, знак, показ блоков [data-show-state]
  function applyState(root, state) {
    root.dataset.state = state;
    var info = STATE[state] || STATE.off;
    root.querySelectorAll('[data-role="state-label"]').forEach(function (l) { l.textContent = info.label; });
    root.querySelectorAll(".power").forEach(function (b) {
      b.innerHTML = "";
      b.appendChild(icon("Shield", info.mark, "1.9"));
      b.setAttribute("aria-label", state === "off" ? "Включить защиту" : "Выключить защиту");
    });
    root.querySelectorAll("[data-state-mark]").forEach(function (holder) {
      holder.innerHTML = "";
      holder.appendChild(icon("Shield", info.mark, "1.9"));
    });
    root.querySelectorAll("[data-show-state]").forEach(function (el) {
      el.hidden = el.dataset.showState.split(/\s+/).indexOf(state) < 0;
    });
  }

  function frameFor(screen) {
    var dev = screen.dataset.device || device;
    var phone = dev === "android" || dev === "ios";
    var wrap = document.createElement("div");
    wrap.className = "pg-frame-wrap";
    if (screen.dataset.caption) {
      var cap = document.createElement("div");
      cap.className = "pg-frame-caption";
      cap.textContent = screen.dataset.caption;
      wrap.appendChild(cap);
    }
    var frame = document.createElement("div");
    frame.className = "pg-frame " + (phone ? "is-phone" : "is-window");
    frame.dataset.device = dev;
    if (!phone) {
      var bar = document.createElement("div");
      bar.className = "pg-titlebar";
      if (dev === "macos") {
        bar.innerHTML = '<span class="pg-traffic"><i></i><i></i><i></i></span><span class="pg-titlebar-text">Private Gateway</span>';
      } else if (dev === "linux") {
        bar.innerHTML = '<span class="pg-titlebar-text">Private Gateway</span><span class="pg-win-btn" aria-hidden="true"></span>';
        bar.lastChild.appendChild(icon("Close", null, "2.4"));
        bar.lastChild.firstChild.style.width = bar.lastChild.firstChild.style.height = "12px";
      } else {
        var logo = icon("Shield", "Mark.Ok", "2");
        logo.setAttribute("class", "pg-icon pg-titlebar-icon");
        bar.appendChild(logo);
        bar.insertAdjacentHTML("beforeend", '<span class="pg-titlebar-text">Private Gateway</span>' +
          '<span class="pg-win-btn" aria-hidden="true">&#8212;</span><span class="pg-win-btn close" aria-hidden="true"></span>');
        var x = icon("Close", null, "1.6");
        x.style.width = x.style.height = "14px";
        bar.lastChild.appendChild(x);
      }
      frame.appendChild(bar);
    }
    var app = document.createElement("div");
    app.className = "pg-app";
    app.dataset.stateRoot = "";
    app.innerHTML = '<div class="pg-blob"></div><div class="pg-glass"></div>';
    while (screen.firstChild) { app.appendChild(screen.firstChild); }
    if (screen.style.height) { app.style.height = screen.style.height; }
    frame.appendChild(app);
    if (phone) {
      frame.insertAdjacentHTML("beforeend", '<div class="pg-statusbar"><span>9:41</span><span class="pg-statusbar-icons"></span></div>' +
        '<div class="pg-notch"></div><div class="pg-homebar"></div>');
      var icons = frame.querySelector(".pg-statusbar-icons");
      icons.appendChild(icon("Net.Mobile", null, "2"));
      icons.appendChild(icon("Net.WiFi", null, "2"));
    }
    wrap.appendChild(frame);
    app.querySelectorAll(".pg-island").forEach(function (island) { buildIsland(island, dev); });
    applyState(app, screen.dataset.state || "connected");
    return wrap;
  }

  function buildToolbar() {
    var board = BOARDS.filter(function (b) { return location.pathname.endsWith("/" + b.file); })[0];
    var bar = document.createElement("nav");
    bar.className = "pg-toolbar";
    var title = document.createElement("span");
    title.className = "pg-toolbar-title";
    title.textContent = board ? board.n + " · " + board.title : document.title;
    bar.appendChild(title);
    DEVICES.forEach(function (d) {
      var a = document.createElement("a");
      a.href = location.pathname.split("/").pop() + "?device=" + d.id;
      a.textContent = d.title;
      if (d.id === device) { a.setAttribute("aria-current", "true"); }
      bar.appendChild(a);
    });
    var all = document.createElement("a");
    all.href = withDevice(board ? "../index.html" : "index.html");
    all.textContent = "Все экраны";
    bar.appendChild(all);
    if (board) {
      var i = BOARDS.indexOf(board);
      if (i > 0) { bar.insertAdjacentHTML("beforeend", '<a href="' + withDevice(BOARDS[i - 1].file) + '">‹ ' + BOARDS[i - 1].n + "</a>"); }
      if (i < BOARDS.length - 1) { bar.insertAdjacentHTML("beforeend", '<a href="' + withDevice(BOARDS[i + 1].file) + '">' + BOARDS[i + 1].n + " ›</a>"); }
    }
    return bar;
  }

  function closeAll(app) {
    app.querySelectorAll(".scrim, .sheet, .dialog, .popup").forEach(function (el) { el.classList.remove("is-open"); });
  }

  function open(app, id, trigger) {
    var el = app.querySelector("#" + CSS.escape(id)) || document.getElementById(id);
    if (!el) { console.warn("Нет элемента " + id); return; }
    if (!el.classList.contains("popup")) {
      app.querySelectorAll(".popup").forEach(function (p) { p.classList.remove("is-open"); });
      var scrim = app.querySelector(":scope > .scrim");
      if (!scrim) {
        scrim = document.createElement("div");
        scrim.className = "scrim";
        scrim.dataset.close = "";
        app.appendChild(scrim);
      }
      scrim.style.zIndex = el.classList.contains("dialog") ? "65" : "50";
      scrim.classList.add("is-open");
    } else if (trigger) {
      var a = app.getBoundingClientRect(), t = trigger.getBoundingClientRect();
      el.style.top = (t.bottom - a.top + 6) + "px";
      el.style.right = Math.max(8, a.right - t.right) + "px";
      el.style.left = "auto";
      el._trigger = trigger;
    }
    el.classList.add("is-open");
  }

  function toast(app, text) {
    var t = app.querySelector(":scope > .toast");
    if (!t) {
      t = document.createElement("div");
      t.className = "toast";
      app.appendChild(t);
    }
    t.textContent = text;
    t.classList.add("is-open");
    clearTimeout(t._timer);
    t._timer = setTimeout(function () { t.classList.remove("is-open"); }, 1800);
  }

  function setSwitch(sw, on) {
    sw.classList.toggle("on", on);
    sw.setAttribute("aria-checked", on ? "true" : "false");
  }

  function onClick(e) {
    var el = e.target.closest("button, a, [data-nav], [data-open], [data-close], .check, .segment, .switch, .tab, .fav");
    if (!el) { return; }
    var app = el.closest(".pg-app");
    if (el.matches("[data-nav]")) {
      e.preventDefault();
      location.href = withDevice(el.dataset.nav);
      return;
    }
    if (!app) { return; }
    if (el.matches("[data-close]")) { closeAll(app); }
    if (el.matches("[data-confirm-yes]")) {
      var pending = app._pending;
      app._pending = null;
      closeAll(app);
      if (pending) { pending(); }
      return;
    }
    if (el.matches("[data-power]")) {
      var root = el.closest("[data-state-root]");
      var s = root.dataset.state;
      if (s === "off" || s === "error" || s === "blocked" || s === "strict") {
        applyState(root, "connecting");
        setTimeout(function () { applyState(root, "connected"); }, 1400);
      } else { applyState(root, "off"); }
      return;
    }
    if (el.matches("[data-set-state]")) {
      applyState(el.closest("[data-state-root]"), el.dataset.setState);
    }
    if (el.classList.contains("switch")) {
      var turnOn = !el.classList.contains("on");
      var confirm = turnOn ? el.dataset.confirmOn : el.dataset.confirmOff;
      if (confirm) {
        app._pending = function () { setSwitch(el, turnOn); };
        open(app, confirm);
      } else { setSwitch(el, turnOn); }
      return;
    }
    if (el.classList.contains("segment")) {
      var group = el.closest(".segments");
      var apply = function () {
        group.querySelectorAll(".segment").forEach(function (s) {
          s.classList.toggle("on", s === el);
          s.setAttribute("aria-checked", s === el ? "true" : "false");
        });
      };
      if (el.dataset.confirm && !el.classList.contains("on")) {
        app._pending = apply;
        open(app, el.dataset.confirm);
      } else { apply(); }
      return;
    }
    if (el.classList.contains("check")) { el.classList.toggle("on"); return; }
    if (el.classList.contains("fav")) { el.classList.toggle("on"); return; }
    if (el.classList.contains("tab")) {
      var tabs = el.closest(".tabs");
      tabs.querySelectorAll(".tab").forEach(function (t) { t.classList.toggle("on", t === el); });
      if (el.dataset.panel) {
        app.querySelectorAll("[data-tab-panel]").forEach(function (p) {
          p.style.display = p.dataset.tabPanel === el.dataset.panel ? "" : "none";
        });
      }
      return;
    }
    if (el.classList.contains("btn-chip")) {
      var was = el.classList.contains("selected");
      el.closest(".chips").querySelectorAll(".btn-chip").forEach(function (c) { c.classList.remove("selected"); });
      var info = app.querySelector("[data-chip-info]");
      if (!was) {
        el.classList.add("selected");
        if (info) {
          info.querySelector("[data-chip-title]").textContent = el.querySelector(".chip-title").textContent;
          info.querySelector("[data-chip-text]").textContent = el.dataset.info || "";
          info.style.display = "";
        }
      } else if (info) { info.style.display = "none"; }
      return;
    }
    if (el.matches(".popup .btn-option")) {
      var popup = el.closest(".popup");
      popup.querySelectorAll(".btn-option").forEach(function (o) { o.classList.toggle("current", o === el); });
      var trigger = popup._trigger;
      if (trigger && el.dataset.value) {
        var label = trigger.querySelector("[data-select-label]");
        if (label) { label.textContent = el.dataset.value; }
      }
      if (el.dataset.confirm) {
        popup.classList.remove("is-open");
        open(app, el.dataset.confirm);
        return;
      }
      popup.classList.remove("is-open");
      if (el.dataset.toast) { toast(app, el.dataset.toast); }
      return;
    }
    if (el.matches("[data-open]")) {
      var target = app.querySelector("#" + CSS.escape(el.dataset.open));
      if (target && target.classList.contains("popup") && target.classList.contains("is-open")) {
        target.classList.remove("is-open");
      } else { open(app, el.dataset.open, el); }
    }
    if (el.matches("[data-spin]")) {
      var spinning = el.querySelector(".pg-icon");
      if (spinning && !spinning.classList.contains("spin")) {
        spinning.classList.add("spin");
        setTimeout(function () { spinning.classList.remove("spin"); }, 1500);
      }
    }
    if (el.matches("[data-reveal]")) {
      var scope = el.closest("[data-reveal-scope]") || app;
      var shown = el.dataset.revealed === "1";
      el.dataset.revealed = shown ? "0" : "1";
      scope.querySelectorAll("[data-masked]").forEach(function (m) { m.textContent = shown ? m.dataset.masked : m.dataset.plain; });
      el.innerHTML = "";
      el.appendChild(icon(shown ? "Eye" : "EyeOff", null, "2"));
      el.firstChild.style.width = el.firstChild.style.height = "18px";
    }
    if (el.matches("[data-toast]")) { toast(app, el.dataset.toast); }
    if (el.matches("[data-goto]")) {
      app.querySelectorAll("[data-page]").forEach(function (p) { p.hidden = p.dataset.page !== el.dataset.goto; });
    }
  }

  function closeOnOutside(e) {
    document.querySelectorAll(".popup.is-open").forEach(function (p) {
      if (!p.contains(e.target) && (!p._trigger || !p._trigger.contains(e.target))) { p.classList.remove("is-open"); }
    });
  }

  function init() {
    var body = document.body;
    if (body.dataset.gallery !== undefined) { buildGallery(); return; }
    body.classList.add("pg-board");
    if (!thumb) { body.insertBefore(buildToolbar(), body.firstChild); }
    else { body.style.padding = "16px"; }
    renderIcons(document);
    document.querySelectorAll(".pg-screens").forEach(function (holder) {
      holder.classList.add("pg-frames");
      Array.prototype.slice.call(holder.querySelectorAll(":scope > .pg-screen")).forEach(function (screen) {
        holder.replaceChild(frameFor(screen), screen);
      });
    });
    document.addEventListener("click", function (e) { closeOnOutside(e); onClick(e); });
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape") { document.querySelectorAll(".pg-app").forEach(closeAll); }
    });
  }

  function buildGallery() {
    var body = document.body;
    body.classList.add("pg-board");
    var bar = document.createElement("nav");
    bar.className = "pg-toolbar";
    bar.innerHTML = '<span class="pg-toolbar-title">Private Gateway — макет v1.1 (HTML)</span>';
    DEVICES.forEach(function (d) {
      var a = document.createElement("a");
      a.href = "index.html?device=" + d.id;
      a.textContent = d.title;
      if (d.id === device) { a.setAttribute("aria-current", "true"); }
      bar.appendChild(a);
    });
    body.insertBefore(bar, body.firstChild);
    var host = document.querySelector("[data-gallery-host]") || body;
    var group = null, grid = null;
    BOARDS.forEach(function (b) {
      if (b.group !== group) {
        group = b.group;
        var h = document.createElement("h2");
        h.className = "pg-gallery-group t-title";
        h.textContent = group;
        host.appendChild(h);
        grid = document.createElement("div");
        grid.className = "pg-gallery";
        host.appendChild(grid);
      }
      var a = document.createElement("a");
      a.href = withDevice("boards/" + b.file);
      a.innerHTML = '<div class="pg-thumb"><iframe loading="lazy" tabindex="-1" title=""></iframe></div><span class="t-strong"></span>';
      a.querySelector("iframe").src = "boards/" + b.file + "?device=" + device + "&thumb=1";
      a.querySelector("span").textContent = b.n + " · " + b.title;
      grid.appendChild(a);
    });
  }

  window.PG = { boards: BOARDS, icon: icon, applyState: applyState, toast: toast };
  if (document.readyState === "loading") { document.addEventListener("DOMContentLoaded", init); } else { init(); }
})();
