# Решения, принятые при согласовании макета

Черновик для ROADMAP v1.1. Перенести в README (ROADMAP) после того, как будет готов код десктопа на C# · Avalonia.
Макет: [docs/design](../design/README.md), токены: [shared/specifications/design-tokens.json](../../shared/specifications/design-tokens.json).

## Продукт
- Личный продукт. Модель угроз — анонимность в интернете.
- Задачи ведутся в Markdown в репозитории, без GitHub Issues/Projects.
- Интерфейс: только светлая тема, языки RU и EN.

## Ядро и протоколы
- Основное ядро — **Xray-core** (в ROADMAP v1.0 кандидатом был sing-box).
- Нужны Hysteria2, TUIC, WireGuard — для них, вероятно, второе ядро sing-box. Окончательно — после технического спайка.
- **Своего кода на Go нет.** Ядра берутся готовыми сборками:
  - десктоп — программы `xray` / `sing-box`, служба запускает их отдельным процессом;
  - Android и iOS — готовая обертка XTLS/libXray (Go-библиотека через gomobile).
  - Скрипт `scripts/` скачивает сборки ядер и сверяет контрольные суммы.
- Разбор ссылок и подписок — на C# в `Client.Network`. Одинаковое поведение на всех ОС держат общие эталонные примеры в `shared/testdata` (обезличенные ссылки и подписки + ожидаемый результат), а не общий код. На мобильных разбор переносится на Kotlin и Swift по тем же примерам; вынос в общий модуль — решение на этапе мобильных версий.

## Защита по умолчанию
- Блок при обрыве (Kill Switch) включен.
- Доступ к локальной сети запрещен.
- «Трафик зашифрован» вычисляется по протоколу: VLESS без TLS/REALITY не считается зашифрованным.
- Внешний IP показывается измеренный, а не предполагаемый.

## Платформы
- Десктоп: C# · .NET 10 · Avalonia — Windows, macOS, Linux. Один интерфейс; отличаются рамка окна, трей или строка меню, сочетания клавиш, способ установки службы.
- macOS: туннель и блок при обрыве — системное расширение Network Extension (на практике Swift, общий код с iOS); запасной вариант — служба на C# через launchd + utun + pf. Решение на спайке macOS.
- Android: Kotlin · Jetpack Compose · VpnService, **Android 9+**.
- iOS: Swift · SwiftUI · Network Extension (Packet Tunnel), **iOS 16+**; кнопка в Пункте управления — iOS 18+.
- Android: блок при обрыве — системный («Постоянная VPN» + «Блокировать подключения без VPN»); приложение открывает эти настройки и показывает, включено ли.
- iOS: блок при обрыве — `includeAllNetworks` из приложения; Push Apple и сотовые звонки iOS по умолчанию пускает мимо туннеля.
- iOS: в App Store VPN-приложения принимают только от организаций; для себя — Xcode или TestFlight на платном аккаунте Apple Developer.
- 2026-10-25: подпись кода десктопа (Windows Authenticode, macOS Developer ID и нотаризация, подписанные артефакты
  выпуска) отложена — сейчас не нужна, сертификата нет. Пункты — в ROADMAP, раздел «Отложено (в перспективе)».
- 2026-10-25: macOS без подписи — режим VPN через службу на C# под launchd: TUN — sing-box (utun), блок при
  обрыве — pf. Network Extension — позже, вместе с подписью (раздел «Отложено»).
- 2026-10-25: iOS без аккаунта Apple Developer — приложение и расширение Packet Tunnel пишутся, собираются и
  проверяются в CI на симуляторе (экраны, подписки, логика); запуск туннеля на iPhone и TestFlight — когда будет
  аккаунт.

## Новые экраны
- 2026-10-25: Network Inspector (этап 7) и Network Lab (этап 8) в макете нет — экраны рисует агент по
  дизайн-системе макета (токены, компоненты, доски 23–24), тексты RU/EN; снимки — в отчете, пользователь смотрит
  и правит после.

## Стек

### Десктоп (C#)
| Задача | Выбор |
|---|---|
| Платформа | .NET 10 (LTS), C# 14 |
| Интерфейс | Avalonia 12 (12.1.4 — последняя стабильная на 23.10.2026), шрифты Inter и JetBrains Mono встроены |
| MVVM | CommunityToolkit.Mvvm |
| DI, настройки, фоновые задачи | Microsoft.Extensions.Hosting / DependencyInjection / Options |
| Журнал | Serilog, маскирование ключей, UUID и адресов подписок |
| База | SQLite: Microsoft.Data.Sqlite + Dapper, миграции — пронумерованные SQL-файлы |
| Секреты | Windows — DPAPI, macOS — Keychain, Linux — Secret Service |
| Интерфейс ↔ служба | gRPC (protobuf) через named pipe (Windows) / Unix-сокет (Linux, macOS); контракт — `shared/specifications/ipc` |
| Служба | служба Windows, systemd (Linux) |
| D-Bus (Linux) | Tmds.DBus: systemd-resolved, NetworkManager, polkit, уведомления |
| Трей | TrayIcon Avalonia + свое всплывающее меню в стиле макета |
| Уведомления | Windows — toast, Linux — freedesktop, macOS — UserNotifications |

### Сеть и защита
| Задача | Выбор |
|---|---|
| Ядро | Xray-core (MPL-2.0) отдельным процессом |
| Hysteria2, TUIC, WireGuard | sing-box (GPL-3.0) вторым процессом — после спайка |
| TUN Windows | Wintun |
| TUN → ядро | tun2socks / hev-socks5-tunnel или TUN-вход sing-box — выбор на спайке |
| Блок при обрыве | Windows — WFP (постоянные и загрузочные фильтры), Linux — nftables, macOS — Network Extension |
| Определение сети | Windows — WLAN API + Network List Manager, Linux — NetworkManager, macOS — NWPathMonitor |
| Правила маршрутизации | geoip.dat / geosite.dat |

### Android / iOS
| Задача | Android | iOS |
|---|---|---|
| Язык, интерфейс | Kotlin 2, Jetpack Compose, Roboto | Swift 6, SwiftUI, SF Pro |
| Архитектура | ViewModel + StateFlow, Hilt | ObservableObject (минимум iOS 16) |
| Туннель | VpnService + TUN → SOCKS, libXray .aar | Packet Tunnel + TUN → SOCKS, libXray .xcframework (лимит памяти около 50 МБ) |
| Хранение | Room, DataStore, Android Keystore | GRDB, Keychain + App Group |
| Быстрый доступ | плитка в шторке, виджет Glance | WidgetKit, Пункт управления (iOS 18+) |
| QR | ZXing (без сервисов Google) | VisionKit |

### Общее, тесты, выпуск
| Задача | Выбор |
|---|---|
| Токены | `design-tokens.json` → Tokens.axaml / Theme.kt / Tokens.swift (генератор) |
| Тексты RU/EN | общий JSON → .resx / strings.xml / String Catalog |
| Эталоны разбора | `shared/testdata` — обязательны для всех реализаций |
| Тесты .NET | xUnit, Shouldly, NSubstitute, Verify (снимки конфигов), Avalonia.Headless, проверка запрещенных зависимостей |
| Линтеры | dotnet format + анализаторы Roslyn, ktlint, SwiftLint |
| Секреты в репозитории | gitleaks в CI |
| CI | GitHub Actions: Windows, Ubuntu, macOS |
| Пакеты | Windows — MSI (WiX), Linux — .deb/.rpm (nfpm), macOS — .dmg с подписью и нотаризацией |
| Обновления | GitHub Releases + проверка подписи пакета |

Лицензии: Xray — MPL-2.0, Avalonia — MIT, Inter и JetBrains Mono — OFL, SmartBase — Apache-2.0. sing-box — GPL-3.0: для личного использования без ограничений; при распространении держать его отдельным процессом.

## Интерфейс
- Макет v1.0 согласован 23.10.2026 — 26 досок, см. [docs/design](../design/README.md).
- Шрифты: десктоп — Inter и JetBrains Mono (встроены); телефоны — системные (Roboto / Roboto Mono, SF Pro / SF Mono).
- Уведомления системные, по умолчанию «Только важные»; настройка в «Настройки → Основные».
- Мобильные экраны, кроме главного, отдельно не рисуются — устроены так же.

## Работа агентов
- 2026-10-24: агенты делают все самостоятельно и по плану (ROADMAP в README.md): готовую и
  проверенную работу сами коммитят, отправляют и вливают в `master` перемоткой, затем берут
  следующий пункт. Развилки решают сами и пишут в [AGENT-DECISIONS.md](AGENT-DECISIONS.md).
  Без согласия — только перезапись отправленной истории и изменения на компьютере пользователя
  (служба, блок при обрыве, брандмауэр, маршруты, DNS). Подробно — [AGENTS.md](../../AGENTS.md).

## Открыто
- Вид выбранного пункта в выпадающих списках: заливка / полоска / кружок (в макете — заливка).
- Подменю в трее: вправо или влево по месту / внутри меню (в макете — по месту).
- Оставшиеся «подписка» в текстах.
- Поддержка Windows 10.
