# Макет интерфейса v1.1 — HTML и CSS

Интерактивный макет всех 26 экранов: вид программы на 2026-11-01 со всеми правками после макета v1.0.
Открывается в любом браузере двойным щелчком по [`index.html`](index.html), без интернета и без среды Claude.
Вверху каждой страницы — выбор устройства: **Windows · macOS · Linux · Android · iOS**. Экран один и тот же на всех
устройствах: меняются только рамка ОС, шрифт платформы (десктоп — Inter и JetBrains Mono, Android — Roboto, iOS — SF)
и поля по токенам (`space.windowSide` / `windowSideMobile`, островок `layout.island.*Mobile`).

Макет v1.0 (`../mockup/*.dc.html`, `../screens/*.png`) заморожен как история; актуальный — этот.

## Что где лежит

```
docs/design/html/
├── index.html            — все 26 экранов (живые миниатюры), выбор устройства
├── boards/NN-имя.html     — экран на доску: 01-main … 26-mobile-system
└── assets/
    ├── tokens.css         — переменные CSS из design-tokens.json   (генерируется, руками не править)
    ├── icons.js           — контуры значков из Icons.axaml          (генерируется, руками не править)
    ├── app.css            — библиотека блоков (как Styles.axaml программы)
    ├── app.js             — рамки устройств, островок, интерактивность
    └── fonts/             — Inter и JetBrains Mono (те же файлы, что в программе) и их OFL
```

`tokens.css` и `icons.js` собирает `dotnet run scripts/gen-design-html.cs`. В шапке у них SHA-256 источника:
тест `ThemeAndMarkupTests` падает, если токены или значки программы поменяли, а макет не пересобрали. Тот же тест
не пускает в `app.css` и доски шестнадцатеричные цвета и `rgba()`: цвет — только `var(--pg-…)`.

## Как устроена доска

```html
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>2 · Серверы — Private Gateway</title>
<link rel="stylesheet" href="../assets/tokens.css">
<link rel="stylesheet" href="../assets/app.css">
<script src="../assets/icons.js"></script>
<script src="../assets/app.js" defer></script>
</head>
<body>
<p class="pg-board-note">Что показывает доска и что на ней нажимается.</p>
<div class="pg-screens">
  <section class="pg-screen" data-state="connected" data-caption="Подпись над рамкой">
    <div class="pg-page"> … содержимое экрана … <div class="pg-page-end"></div></div>
    … листы, диалоги, меню этого экрана …
    <div class="pg-island" data-active="servers"></div>
  </section>
  <!-- можно несколько экранов в ряд: состояния, шаги, варианты -->
</div>
</body>
</html>
```

- `app.js` оборачивает каждый `.pg-screen` в рамку выбранного устройства. Свое устройство у экрана —
  `data-device="windows|macos|linux|android|ios"` (доски 20–22, 25–26 показывают ОС рядом).
- `data-state` — состояние защиты экрана: `connected`, `connecting`, `reconnecting`, `blocked`, `strict`, `error`,
  `off`, `proxy`, `paused`. От него цвет щита, подпись под ним и знак в щите (`data-state-mark`), блоки
  `data-show-state="connected connecting"` видны только в этих состояниях. Щит островка переключает защиту.
- `.pg-island` с `data-active="home|servers|check|settings"` — островок с щитом; пункты ведут на доски 1, 2, 7, 4.
  На листах и экранах без островка (первый запуск, ошибки) его нет — как в программе.
- `.pg-page` прокручивается без полос, `.pg-page-end` — запас 160 под островок.

## Интерактивность (атрибуты, без своего JS на доске)

| Атрибут / класс | Что делает |
| --- | --- |
| `data-nav="04-settings.html"` | переход на другую доску (устройство сохраняется) |
| `.switch` | тумблер; `data-confirm-off="id"` / `data-confirm-on="id"` — сначала диалог, подтверждает кнопка `data-confirm-yes` |
| `.segments > .segment` | сегменты; `data-confirm="id"` у сегмента — переход только после подтверждения (как «Только прокси») |
| `.check` | флажок |
| `data-open="id"` | открыть `.dialog`, `.sheet` (с затемнением) или `.popup` (меню у кнопки) этого экрана |
| `data-close` | закрыть слой; щелчок по затемнению и Esc — тоже |
| `.popup .btn-option[data-value]` | выбрать пункт: текст в `[data-select-label]` кнопки-владельца, пункт — `.current` |
| `.tabs > .tab[data-panel]` + `[data-tab-panel]` | вкладки |
| `.btn-chip[data-info]` + `[data-chip-info]` | плитка проверки: выбор и пояснение под плитками |
| `data-toast="Текст"` | всплывающая подсказка сверху |
| `data-spin` | значок кнопки крутится 1,5 с («Обновить серверы» — кнопка не сжимается: `.btn-icon.steady`) |
| `data-reveal` + `[data-masked][data-plain]` в `[data-reveal-scope]` | глаз: скрыто — звездочками, как в программе |
| `data-set-state="off"` | перевести экран в состояние защиты |
| `data-goto="step2"` + `[data-page="step2"]` | шаги внутри экрана (страницы `.pg-page` с `data-page`, лишние — `hidden`) |
| `.fav` | звезда избранного |

## Блоки (`app.css`) — те же, что в программе

- Текст: `.t-brand .t-h1 .t-status .t-title .t-strong .t-small .t-caption .t-overline .t-mono .t-mono-large`, цвета
  `.t-muted .t-accent .t-success .t-warning .t-caution .t-danger`.
- Значок: `<i data-icon="Shield" data-detail="Mark.Ok" data-stroke="1.9" data-size="26"></i>` — имена из `Icons.axaml`
  без «Icon.» (`Nav.Home`, `ChevronRight`, `Refresh`, `Copy`, `Eye`, `EyeOff`, `Star`, `Trash`, `Qr`, …).
- Карточка `.card`, разделитель `.divider`, подпись группы `.t-overline.group-label`, полоса `.banner.caution|warning|danger|success|accent`,
  бейдж `.badge.accent|caution|success|warning|danger|muted|country`, метка `.pill.*`.
- Кнопки (наведение — синий текст и значки, без серой заливки; недоступные — серый текст, без фона):
  `.btn-icon` (`.tinted`, `.steady`), `.btn-plain` (строка во всю ширину, стрелка `.chevron` справа), `.btn-link`
  (`.tone-success|warning|danger|caution`), `.btn-select`, `.btn-small` (`.soft`), `.btn-option` (`.current` — синим
  полужирным, без заливки), `.btn-primary`, `.btn-secondary` (`.danger`), `.btn-tile`, `.btn-chip` + `.tone-*`.
- Управление: `.switch` (`.on`), `.segments > .segment` (`.on`), `.check` (`.on`, внутри `.check-box` с `Chip.Ok`), `.input`,
  `.search`, строка настройки `.setting` (`.setting-text > .setting-title + .setting-caption`, справа элемент),
  строка-ссылка `.link-row` (`.row-icon`, `.chevron`), вкладки `.tabs > .tab`, строка сервера `.server-row` (`.current`),
  задержка `.latency.good|mid|bad|none`, журнал `.log`, `.spinner`.
- Слои: `.sheet` (с `.grabber`), `.dialog` (`.dialog-icon.danger|warning|caution|accent`, `.dialog-actions`), `.popup`, `.toast`.
- Раскладка: `.pg-pad` (поля окна), `.pg-stack`, `.pg-row`, `.pg-gap-*`, `.pg-grow`, `.pg-ellipsis`.

## Правила

1. Цвет, шрифт, скругление, тень — только `var(--pg-…)` из `tokens.css`; шестнадцатеричных цветов и `rgba()` нет.
2. Тексты — из `shared/specifications/localization/ru.json`, как в программе; буквы «е с точками» нет.
3. Данные — демо программы (`DemoServerCatalog`): адреса только из диапазонов документации (192.0.2.0/24,
   198.51.100.0/24, 203.0.113.0/24), домены example.com/example.org. Скрытое — звездочками.
4. Экран один и тот же на всех устройствах: никаких веток «если Android». Отличия ОС — только рамка и шрифт
   (`app.js`), системные окна ОС — на досках 20–22 и 26.
5. Новый блок — сначала в `app.css`, потом на досках.
