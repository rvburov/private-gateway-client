# Макет интерфейса — v1.0

**Статус:** согласован 23.10.2026 · заморожен. Изменения — только новой версией макета.

Макет покрывает десктоп (C# · Avalonia: Windows, macOS, Linux) и мобильные версии (Android · Kotlin + Compose, iOS · Swift + SwiftUI).
Интерактивная версия — артефакт Claude «Private Gateway Client — макет» (доступ только у владельца).

## Что где лежит

```
docs/design/
├── README.md        — этот файл: список экранов, правила, открытые вопросы
├── screens/         — PNG всех 26 досок макета (снимок v1.0)
└── mockup/          — исходники макета: canvas.json + *.dc.html (по файлу на доску)

shared/specifications/
└── design-tokens.json — единый источник цветов, шрифтов, отступов, скруглений, теней
```

`design-tokens.json` — источник правды. Из него собираются ресурсы каждой платформы:
`Tokens.axaml` (Avalonia), `Theme.kt` (Compose), `Tokens.swift` (SwiftUI). Цвета и размеры в коде вручную не задаются.

## Экраны

### Основные экраны (Windows)

| № | Экран | Исходник |
|---|---|---|
| 1 | [Главная](screens/01-main.png) | [`Main.dc.html`](mockup/Main.dc.html) |
| 2 | [Серверы](screens/02-servers.png) | [`Servers.dc.html`](mockup/Servers.dc.html) |
| 3 | [Добавить профиль](screens/03-import.png) | [`Import.dc.html`](mockup/Import.dc.html) |
| 4 | [Настройки](screens/04-settings.png) | [`Settings.dc.html`](mockup/Settings.dc.html) |
| 5 | [Защита и утечки](screens/05-protection.png) | [`Protection.dc.html`](mockup/Protection.dc.html) |
| 6 | [Подключение и ядро](screens/06-advanced.png) | [`Advanced.dc.html`](mockup/Advanced.dc.html) |
| 7 | [Проверка и логи](screens/07-diagnostics.png) | [`Diagnostics.dc.html`](mockup/Diagnostics.dc.html) |
| 8 | [Меню в трее + подменю](screens/08-tray.png) | [`Tray.dc.html`](mockup/Tray.dc.html) |
| 9 | [Состояния главного экрана](screens/09-states.png) | [`States.dc.html`](mockup/States.dc.html) |

### Дополнительные экраны и сценарии

| № | Экран | Исходник |
|---|---|---|
| 10 | [Первый запуск](screens/10-onboarding.png) | [`Onboarding.dc.html`](mockup/Onboarding.dc.html) |
| 11 | [Карточка сервера](screens/11-server-card.png) | [`ServerCard.dc.html`](mockup/ServerCard.dc.html) |
| 12 | [Профили и подписки](screens/12-subscriptions.png) | [`Subscriptions.dc.html`](mockup/Subscriptions.dc.html) |
| 13 | [Маршрутизация](screens/13-routing.png) | [`Routing.dc.html`](mockup/Routing.dc.html) |
| 14 | [Резервная копия](screens/14-backup.png) | [`Backup.dc.html`](mockup/Backup.dc.html) |
| 15 | [О программе и обновление](screens/15-about.png) | [`About.dc.html`](mockup/About.dc.html) |
| 16 | [Ошибки](screens/16-errors.png) | [`Errors.dc.html`](mockup/Errors.dc.html) |
| 17 | [Главная: прокси и вход в Wi-Fi](screens/17-main-variants.png) | [`MainVariants.dc.html`](mockup/MainVariants.dc.html) |
| 18 | [Диалоги подтверждения](screens/18-dialogs.png) | [`Dialogs.dc.html`](mockup/Dialogs.dc.html) |
| 19 | [Уведомления: какие бывают](screens/19-notifications.png) | [`Notifications.dc.html`](mockup/Notifications.dc.html) |

### Три ОС: Windows · macOS · Linux

| № | Экран | Исходник |
|---|---|---|
| 20 | [Окно на Windows, macOS, Linux](screens/20-platforms.png) | [`Platforms.dc.html`](mockup/Platforms.dc.html) |
| 21 | [Трей, строка меню и установка службы по ОС](screens/21-platform-specifics.png) | [`PlatformSpecifics.dc.html`](mockup/PlatformSpecifics.dc.html) |
| 22 | [Уведомления на Windows, macOS, Linux](screens/22-notify-os.png) | [`NotifyOS.dc.html`](mockup/NotifyOS.dc.html) |

### Дизайн-система и брендбук

| № | Экран | Исходник |
|---|---|---|
| 23 | [Дизайн-система: основы](screens/23-ds-foundations.png) | [`DSFoundations.dc.html`](mockup/DSFoundations.dc.html) |
| 24 | [Дизайн-система: компоненты](screens/24-ds-components.png) | [`DSComponents.dc.html`](mockup/DSComponents.dc.html) |

### Мобильные: Android (Kotlin) и iOS (Swift)

| № | Экран | Исходник |
|---|---|---|
| 25 | [Мобильные: главный экран](screens/25-mobile-main.png) | [`MobileMain.dc.html`](mockup/MobileMain.dc.html) |
| 26 | [Android и iOS: системные окна и технологии](screens/26-mobile-system.png) | [`MobileSystem.dc.html`](mockup/MobileSystem.dc.html) |

## Главные решения по интерфейсу

- Главный экран: карточка состояния с 4 чипами (весь трафик, DNS, IPv6, блок при обрыве), карточка «Сайты видят ваш IP» вместе с выбранным сервером, карточка скорости.
- Внизу островок-меню: Главная · Серверы · кнопка-щит 80 px · Проверка · Настройки. Содержимое прокручивается под островком, полосы прокрутки скрыты.
- Цвета состояний: зеленый — защищено, синий — подключение, оранжевый — блокировка, желтый — прокси или пауза, красный — выключено или ошибка. Статус всегда показан иконкой и текстом.
- Только светлая тема. Языки RU и EN.
- Шрифты: десктоп — Inter и JetBrains Mono (встроены), телефоны — системные (Roboto / Roboto Mono, SF Pro / SF Mono). Размеры одинаковые.
- Телефоны: тот же главный экран, ширина 360–440, поля 16, островок над полоской жестов. Остальные мобильные экраны устроены так же и отдельно не рисуются.
- Уведомления системные; по умолчанию «Только важные».
- Кнопка обновления списка называется «Обновить серверы».

## Правила для новых блоков

1. Только токены из `design-tokens.json`, без цветов и размеров вручную.
2. Зона нажатия не меньше 44 × 44.
3. Контраст текста не ниже 4.5 : 1.
4. Состояния: обычное, наведение, нажатие, фокус (Tab), выключено.
5. Статус всегда иконкой и текстом, не только цветом.
6. Тексты на RU и EN: закладывать +30 % длины.
7. Одинаково на Windows, macOS и Linux, кроме рамки окна и трея.

## Открытые вопросы (в макете стоит вариант по умолчанию)

| Вопрос | По умолчанию в макете |
|---|---|
| Вид выбранного пункта в выпадающих списках (экраны 5–6) | заливка (`highlight`); есть варианты `bar`, `radio`. В программе с 2026-10-28 — синий полужирный текст без заливки, наведение — синий текст вместо серой заливки (решение 485, по замечаниям пользователя) |
| Подменю «Сменить сервер» в трее (экран 8) | открывается вправо, у края экрана — влево; есть вариант «внутри меню» |
| Слово «подписка» в «Ссылка на подписку», «Профили и подписки», «Основная подписка» | оставлено как есть |
| Поддержка Windows 10 | в макете «Windows 10 / 11» |

## Как открыть исходники

Файлы `mockup/*.dc.html` — формат дизайн-холста Claude (Design Component): HTML с шаблонами `{{…}}`, `<sc-for>`, `<sc-if>` и классом логики.
Без среды холста они не отрисуются в обычном браузере — для просмотра используйте PNG из `screens/`.
