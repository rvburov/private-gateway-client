# Интерфейс десктопа

Как устроен `desktop/src/Client.UI` (Avalonia 12) и откуда в нем берутся цвета, тексты и данные.

## Тема из токенов

```
shared/specifications/design-tokens.json ──dotnet run scripts/gen-tokens.cs──▶ Client.UI/Themes/Tokens.axaml
```

- `Tokens.axaml` генерируется, руками не правится. В шапке — SHA-256 файла токенов; тест
  `ThemeAndMarkupTests` падает, если токены поменяли, а тему не пересобрали.
- Имена ресурсов: `PgColor.*` / `PgBrush.*` (цвета), `PgFontSize.*`, `PgFontWeight.*`,
  `PgLetterSpacing.*`, `PgSpace.*` / `PgPad.*`, `PgRadius.*`, `PgShadow.*`, `PgLayout.*`,
  `PgFont.Ui` / `PgFont.Mono`.
- `Themes/Styles.axaml` — стили блоков (карточка, текст, кнопки, пункт островка, кнопка-щит) только
  на этих ресурсах. Тест запрещает шестнадцатеричные цвета в любой разметке, кроме `Tokens.axaml`.
- `Themes/Icons.axaml` — контуры иконок 24×24 из макета; элемент `Controls/Icon` рисует их линией
  цвета текста вокруг (как шрифтовые иконки).
- Шрифты: Inter — пакет `Avalonia.Fonts.Inter`; JetBrains Mono — `Assets/Fonts` (OFL, лицензия рядом).

## Тексты RU/EN

- Словари — `shared/specifications/localization/{ru,en}.json`, общие для всех приложений;
  встроены в сборку и читаются `Localization/Loc`.
- В разметке — `{l:L ключ}`, рубрики заглавными — `{l:L ключ, Upper=True}`. Модели представлений
  берут тексты из `Loc.Instance[...]` и пересчитывают их по событию `LanguageChanged`.
- Язык меняется на ходу: разметка привязана к `Loc.Revision`, который растет при смене языка.
- Тесты `LocalizationTests`: ключи совпадают в обоих языках, нет пустых текстов и буквы «е с
  точками», все ключи из разметки и кода есть в словаре, у каждого состояния есть свои подписи.

## Окно и страницы

- `Views/MainWindow` — каркас: фон с цветными пятнами (мягкие радиальные градиенты вместо размытия),
  слой стекла `Glass.Fill`, текущая страница, всплывающая подсказка и `Controls/IslandNav`.
- `IslandNav` — островок навигации; контур считается по ширине окна (`IslandGeometry`), тень — из
  токена `shadow.island`, кнопка-щит и подпись под ней — из `ProtectionViewModel`.
- Страницы — модели представлений (`ShellViewModel.Navigate`), разметка подбирается шаблонами в
  `App.axaml`. Главная — `MainPage` по доске 1 макета; остальные пока заглушки.

## Данные: служба и демо-режим

- Интерфейс узнает о защите только от службы: `IGatewayClient` (Application) — состояние и просьбы
  подключиться, отключиться, проверить. Переходы состояний — `ConnectionStateMachine`.
- Пока службы нет (этап 1), подключена имитация `Infrastructure/Demo/SimulatedGatewayClient` и
  серверы `DemoServerCatalog` (адреса из диапазонов документации). В шапке — значок «демо».
- Кнопка-щит: `ConnectionController.ToggleAsync` — «Выключено» и «Ошибка» включают, остальные
  состояния выключают; нажатие доступно и во время подключения (прерывает его).

## Настройки и данные

- `AppSettings` (Core) — раздел «Основные»: режим, блок при обрыве, автозапуск, подключение при
  запуске, сворачивание в трей, уведомления, язык. По умолчанию — решения из DECISIONS.md.
- `SettingsService` (Application) читает настройки один раз и сохраняет каждое изменение сразу.
- `SqliteSettingsStore` (Infrastructure) — таблица `settings (key, value)`; нечитаемое значение —
  значение по умолчанию и предупреждение в журнале.
- `SqliteDatabase` — миграции `Storage/Migrations/NNNN_*.sql` по порядку, версия в `PRAGMA user_version`.
- Папка данных — `AppPaths`: при запуске из репозитория `.localdata/`, иначе папка пользователя ОС;
  `PG_DATA_DIR` перекрывает обе.
- Журнал — Serilog в `<данные>/logs/client-*.log`, по дню, 7 дней, до 10 МБ; каждая строка целиком
  (с исключением) проходит через `SecretMasker` (`MaskingTextFormatter`).

## Тесты без экрана

`desktop/tests/UI` — Avalonia.Headless с отрисовкой Skia: окно строится как в программе, тесты жмут
кнопки и проверяют подписи, а снимки каждого состояния пишутся в `artifacts/ui-snapshots/` для
сверки с `docs/design/screens/`.

```bash
dotnet test --project desktop/tests/UI
```
