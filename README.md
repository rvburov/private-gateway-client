# PRIVATE GATEWAY CLIENT — MASTER ROADMAP
**Версия:** 1.0 • **Дата:** 17 октября 2026 • **Статус:** план, реализация не подтверждена

## 1. Видение и ограничения
Самостоятельный универсальный прокси/VPN-клиент с ручным выбором выхода, полной системной маршрутизацией, fail-closed защитой и учебно-диагностическим Network Inspector. Не зависит от сервера PRIVATE GATEWAY. Windows MVP → Linux/macOS → Network Inspector/Lab → Android → iOS.

**Стек:** Desktop C#/.NET 10/Avalonia; sing-box как кандидат сетевого ядра; SQLite; Serilog; нативная системная служба и IPC; Android Kotlin/Compose/VpnService; iOS Swift/SwiftUI/Network Extension. Общие спецификации, но не общая UI-кодовая база с мобильными платформами.

## 2. Исходные репозитории — обязательная проверка до реализации
### SmartBase — инженерная база
- [Репозиторий](https://github.com/rvburov/SmartBase)
- [AGENTS.md](https://github.com/rvburov/SmartBase/blob/main/AGENTS.md) — политика параллельной работы, тестирования и Git.
- [Scripts/worktree.sh](https://github.com/rvburov/SmartBase/blob/main/Scripts/worktree.sh) — check/new/sync/merge/remove.
- [Архитектура](https://github.com/rvburov/SmartBase/blob/main/docs/developer/architecture/overview.md)
- [Core](https://github.com/rvburov/SmartBase/tree/main/Core), [Views](https://github.com/rvburov/SmartBase/tree/main/Views), [ViewModels](https://github.com/rvburov/SmartBase/tree/main/ViewModels), [Controls](https://github.com/rvburov/SmartBase/tree/main/Controls)
- [SingleInstance.cs](https://github.com/rvburov/SmartBase/blob/main/Utils/SingleInstance.cs), [Autostart.cs](https://github.com/rvburov/SmartBase/blob/main/Utils/Autostart.cs)
- [InstallService.cs](https://github.com/rvburov/SmartBase/blob/main/Setup/InstallService.cs), [Tests](https://github.com/rvburov/SmartBase/tree/main/Tests), [GitHub Actions](https://github.com/rvburov/SmartBase/tree/main/.github/workflows)
- [Directory.Build.props](https://github.com/rvburov/SmartBase/blob/main/Directory.Build.props), [nuget.config](https://github.com/rvburov/SmartBase/blob/main/nuget.config)
Не переносить специфичные для SmartBase бизнес-модули и тестовые инфраструктуры без необходимости.

### PRIVATE GATEWAY — источник форматов и интеграционных тестов
- [Приватный репозиторий](https://github.com/rvburov/private-gateway) (основная ветка `master`).
- [README](https://github.com/rvburov/private-gateway/blob/master/README.md), [документация клиентов](https://github.com/rvburov/private-gateway/blob/master/docs/clients.md).
- По README подтверждены: Xray, VLESS+REALITY, ссылки VLESS/QR, подписки `/sub/…` и варианты base64 VLESS, Mihomo YAML, sing-box JSON. Использовать как тестовые образцы совместимости, **не** переносить секреты, ключи, реальные URL подписок, UUID устройств или учетные данные.

### Hiddify — исследовательский образец клиента
- [Hiddify App](https://github.com/hiddify/hiddify-app), [README](https://github.com/hiddify/hiddify-app/blob/main/README.md), [лицензия](https://github.com/hiddify/hiddify-app/blob/main/LICENSE.md)
- [lib](https://github.com/hiddify/hiddify-app/tree/main/lib), [Windows](https://github.com/hiddify/hiddify-app/tree/main/windows), [Android](https://github.com/hiddify/hiddify-app/tree/main/android), [iOS](https://github.com/hiddify/hiddify-app/tree/main/ios)
- Изучать конфигурации, подписки, UI-потоки, сетевые состояния, маршрутизацию, TUN и платформенные ограничения.
- **Лицензионный блокер:** LICENSE.md Hiddify содержит дополнительные требования о форке, публикации исходников, некоммерческом использовании и атрибуции. Не копировать Hiddify-код в независимый проект до отдельной юридической проверки/разрешения. Идеи и документированные интерфейсы можно изучать, реализацию создавать самостоятельно либо использовать отдельно лицензированные компоненты. Также проверить лицензии sing-box и каждой зависимости.

### Обязательная запись о переиспользовании
Для каждого модуля вести `docs/sources/REUSE_REGISTER.md`: задача → URL файла/коммита → лицензия → применимость → что переносим/пишем заново → тесты → решение. Не выдавать непроверенную ссылку за подтвержденный файл.

## 3. Структура репозитория
```
AGENTS.md
README.md
desktop/src/Client.Core/
desktop/src/Client.Contracts/
desktop/src/Client.Application/
desktop/src/Client.UI/
desktop/src/Client.Network/
desktop/src/Client.Service/
desktop/src/Client.Infrastructure/
desktop/src/Client.Platform.Windows/
desktop/src/Client.Platform.Linux/
desktop/src/Client.Platform.MacOS/
desktop/tests/{Unit,Integration,Network,UI,Security}/
android/
ios/
shared/specifications/
docs/{architecture,security,network,testing,platform,user,roadmap,sources}/
scripts/
.github/workflows/
.localdata/   # ignored
artifacts/    # ignored
```
Физические границы модулей через отдельные проекты, архитектурные тесты запрещенных зависимостей, запрет прямого управления сетью из UI.

## 4. Этап 0 — репозиторий и правила агентов
- [x] Создать отдельный репозиторий; solution, .NET 10, Avalonia, NuGet, gitignore. — 2026-10-23: `PrivateGateway.slnx`, .NET 10, Avalonia 12.1.4, центральные версии пакетов, кеш NuGet в `packages/`, вывод в `artifacts/`.
- [x] Перенести и **адаптировать** `AGENTS.md` из SmartBase: структура нового проекта, команды сборки, тесты, маршрутизация, правила безопасности. — 2026-10-23: `AGENTS.md` + `CLAUDE.md`, ветка `master`, слои и запрет сети в интерфейсе, общие ресурсы VPN, секреты.
- [x] Перенести и адаптировать `scripts/worktree.sh`; проверить Git Bash/Windows и Linux/macOS. — 2026-10-24: основная ветка определяется сама; `scripts/tests/worktree-smoke.sh` гоняется в CI на Ubuntu, macOS и Windows (Git Bash).
- [x] **Каждый чат — отдельный worktree и ветка, даже при одном агенте.** Основная папка только для main и запуска. — 2026-10-23: правило в `AGENTS.md` (основная ветка — `master`).
- [x] В начале и конце чата `scripts/worktree.sh check`; показывать незакоммиченные и невлитые изменения. — 2026-10-23: правило в `AGENTS.md`, в облаке `check` показывает неотправленное.
- [x] Claude Code: `.claude/worktrees/<task>`; другие: `.agents/worktrees/<task>`. Отдельные ветки. — 2026-10-23: `AGENTS.md`, `scripts/worktree.sh new`, обе папки в `.gitignore`.
- [x] Не трогать чужие ветки, файлы и процессы; никакого `reset --hard`, `clean`, force-remove и удаления без запроса. — 2026-10-23: `AGENTS.md`, пункт 6 и раздел «Git».
- [x] Commit/merge/push — только по разрешению пользователя; при разрешенном merge: commit → sync → tests → merge, подтверждать только после фактического попадания в main. — 2026-10-23: `AGENTS.md`, пункт 4; автор коммитов — пользователь, без подписей ИИ.
- [x] Разделить общие ресурсы: сетевые адаптеры, VPN-служба, порты, firewall, тестовые стенды и кеши; исключить одновременные конфликтующие тесты. — 2026-10-23: `AGENTS.md`, «Общее на одной машине — по одному агенту за раз».
- [x] Документация и ROADMAP обновляются с кодом; ошибки записываются как задачи. — 2026-10-23: правило в `AGENTS.md`; решения — `docs/roadmap/DECISIONS.md`, самостоятельные решения агента — `docs/roadmap/AGENT-DECISIONS.md`.
- [x] Определить границы модулей, IPC-контракты, threat model, лицензионный реестр. — 2026-10-23: `docs/architecture/overview.md` + тесты `Architecture`, `shared/specifications/ipc/gateway_service.proto`, `docs/security/threat-model.md`, `docs/sources/REUSE_REGISTER.md`.
**Готово когда:** чистая сборка, базовые тесты, проверен workflow двух параллельных worktree.
**Итог 2026-10-24:** сборка без предупреждений, 67 тестов (Unit + Architecture), CI на Ubuntu, Windows и macOS зеленый, две рабочие папки проверены на временном репозитории (`scripts/tests/worktree-smoke.sh`).

## 5. Этап 1 — Windows GUI
- [ ] Shell Avalonia, MVVM, DI, навигация, тема, локализация, DPI, доступность.
- [ ] Главный экран, серверы, подписки, настройки, диагностика, логи.
- [ ] SQLite и миграции, секреты через защищенное хранилище ОС, Serilog с маскированием секретов.
- [ ] SingleInstance, tray, фон, автозапуск без отмены явного решения пользователя.
- [ ] UI не владеет привилегиями VPN; состояния `Off/Connecting/Connected/Reconnecting/Blocked/Error`.
- [ ] Unit/UI/headless тесты.
**Готово когда:** UI стабильно запускается, настройки сохраняются, секреты не попадают в логи.
**Ход 2026-10-24:** тема Avalonia генерируется из `design-tokens.json` (`scripts/gen-tokens.cs`), тексты RU/EN из общего словаря с переключением на ходу, каркас окна с островком навигации и кнопкой-щитом, главный экран по макету на имитации службы (значок «демо»), экран «Настройки» с сохранением в SQLite (миграции, папка данных), журнал Serilog с маскированием секретов, экран «Серверы» (поиск, вкладки, фильтр протоколов, сортировка, ручной выбор), карточка сервера (избранное, сведения, переименование, удаление с подтверждением), диалоги подтверждения и «Снять защиту?» в блокировке, тесты без экрана со снимками окна (`desktop/tests/UI`). Дальше: экран проверки; секреты в хранилище ОС; одна копия программы, трей и автозапуск; DPI и доступность.

## 6. Этап 2 — прокси, подписки, выбор выхода
- [ ] Интеграция проверенной версии sing-box через адаптер и контролируемый lifecycle.
- [ ] VLESS, VMess, Trojan, Shadowsocks в пределах поддержки выбранной версии.
- [ ] URL/QR/clipboard/file импорт; sing-box JSON, совместимые Clash/Mihomo форматы, VLESS URI/base64.
- [ ] Валидация, дедупликация, ошибки импорта, безопасное обновление подписок.
- [ ] Каталог серверов, поиск, фильтры, latency, отображение доступности.
- [ ] **Ручной выбор и сохранение сервера**; без скрытого переключения на иной IP/выход.
- [ ] Совместимость с тестовыми конфигурациями PRIVATE GATEWAY без настоящих секретов.
- [ ] Proxy mode как необязательный режим; UI честно сообщает, что он не покрывает весь трафик.
**Готово когда:** выбранный сервер работает в контролируемом прокси-тесте; импорт и обновления проходят тесты.

## 7. Этап 3 — Windows системный VPN и защита
- [ ] Привилегированная Windows-служба, подписанные бинарники, аутентифицированный IPC и проверка полномочий.
- [ ] TUN-адаптер, таблицы маршрутов IPv4/IPv6, DNS, локальная сеть по явной политике.
- [ ] Полное покрытие браузера, VS Code, Claude Code, Git, терминала и фоновых процессов.
- [ ] Fail-closed Kill Switch средствами Windows Firewall/WFP; явно определить допустимые служебные исключения для VPN endpoint.
- [ ] Защита от DNS/IPv6 утечек, split tunneling только отдельной осознанной опцией.
- [ ] Реконнект **только на выбранный сервер**; недоступен — блокировка/уведомление, без автоматического выбора другого.
- [ ] Сбой GUI, sing-box, службы, сети, sleep/wake, Wi-Fi→hotspot, reboot, смена IPv4/IPv6.
- [ ] При VPN OFF корректно возвращать прямую сеть; после аварии — безопасное восстановление с сохранением политики.
- [ ] Учитывать конкурирующие VPN/прокси и отсутствие прав администратора.
**Готово когда:** автоматические тесты на реальной Windows VM подтверждают системное покрытие и отсутствие утечек при сбоях.

## 8. Этап 4 — надежность и эксплуатация
- [ ] Состояния подключения и таймауты, экспоненциальный backoff, сохранение выбора сервера.
- [ ] Autostart/auto-connect отдельно; запуск без UI, tray, уведомления, диагностические коды ошибок.
- [ ] Идемпотентное применение маршрутов и правил, журнал восстановления.
- [ ] Проверка ресурсов, сна, сетевых переходов и конфликтов служб.
**Готово когда:** длительные soak-тесты, восстановление без незапланированного обхода туннеля.

## 9. Этап 5 — сборки, тесты, установщики, релизы
- [ ] CI: lint, formatting, architecture, unit, integration, security, UI, network; результаты без скрытых skipped.
- [ ] GitHub Actions для Windows; позже Linux/macOS; SBOM, контрольные суммы, подписанные артефакты.
- [ ] Installer/uninstaller службы и UI; backup/rollback сетевых правил; не трогать чужие файлы.
- [ ] GitHub Releases, SemVer, release notes, staged update.
- [ ] Аутентификация обновлений, атомарная установка, совместимость версий GUI/службы/ядра, откат.
**Готово когда:** чистая установка, обновление, откат и удаление в VM проходят с восстановлением сети.

## 10. Этап 6 — Linux/macOS
- [ ] Общие Core/Application/UI; платформенные адаптеры и службы.
- [ ] Linux: systemd, TUN, nftables/iptables согласно дистрибутиву, DNS/systemd-resolved, пакеты.
- [ ] macOS: Network Extension и entitlements при необходимости, подпись/notarization, корректная маршрутизация.
- [ ] Одинаковые контрактные тесты: manual exit, kill switch, DNS/IPv6, reconnect, sleep/wake.
**Готово когда:** реальные машины и VM проходят тесты без нарушения платформенных правил.

## 11. Этап 7 — Network Inspector (read-only)
- [ ] Список интерфейсов, MAC, DHCP, шлюзы, DNS, маршруты, IPv4/IPv6.
- [ ] Публичный IP, ASN/провайдер/регион с указанием источника и возможной погрешности.
- [ ] Ping, traceroute, DNS lookup, TCP reachability, TLS diagnostics.
- [ ] Соединения и процессы по возможностям ОС, скорость/объем трафика.
- [ ] Сравнение до/после VPN, диагностика DNS/IPv6/WebRTC с оговорками методики.
- [ ] Экспорт обезличенного отчета; отсутствие секретов и приватных URL.
**Готово когда:** инспектор не меняет настройки сети, результаты воспроизводимы.

## 12. Этап 8 — Network Lab (обучение)
- [ ] Карта устройство→адаптер→роутер→провайдер→VPN→сайт.
- [ ] Различать измеренные и условные участки маршрута.
- [ ] OSI 7 уровней; IP, TCP/UDP, DNS, TLS, HTTP(S), TUN и маршрутизация.
- [ ] Объяснения сетевых событий, анимации, режимы начинающий/эксперт.
- [ ] Сценарии «почему сайт не открывается», «как меняется путь при VPN».
**Готово когда:** объяснения согласованы с фактическими данными, а не выдают модель за измерение.

## 13. Этап 9 — Android
- [ ] Kotlin/Compose, Android VpnService, совместимое сетевое ядро.
- [ ] Подписки, QR, серверы, ручной выбор, DNS, уведомления, фоновая работа.
- [ ] Always-on VPN и Block connections without VPN где поддерживается.
- [ ] Тесты на эмуляторах и устройствах, смена сети/сон/энергосбережение.
- [ ] Ограниченный возможностями Android Network Inspector.
**Готово когда:** Android проходит security/network и lifecycle тесты.

## 14. Этап 10 — iOS
- [ ] Swift/SwiftUI, Network Extension / NEPacketTunnelProvider, entitlements.
- [ ] Подписки, ручной выход, управление туннелем, DNS, восстановление, логи.
- [ ] Подпись, provisioning, TestFlight, проверки Apple.
- [ ] Ограниченный API диагностики, реальные iPhone.
**Готово когда:** стабильный туннель на устройстве, соблюдены правила Apple.

## 15. Этап 11 — стабилизация и выпуск
- [ ] Регрессия всех платформ/протоколов, chaos/network tests, утечки, обновления.
- [ ] Performance, CPU/RAM/battery, доступность UI, локализация.
- [ ] Security review, лицензии, SBOM, подписи, privacy, документация, инструкции, changelog.
- [ ] Релизный чеклист и план поддержки/реагирования на инциденты.
**Готово когда:** релизные критерии выполнены и подтверждены протоколами тестирования.

## 16. Сквозные правила приемки
1. Не заявлять о «системном VPN» на основании работы браузера.
2. Fail-closed поведение проверять на обрывах, не только при нормальном подключении.
3. Без скрытого выбора другого сервера.
4. Не переносить секреты из серверного проекта.
5. Не копировать код Hiddify до лицензионного решения.
6. Никаких тестов «прошло», если тесты skipped/не запускались.
7. Любая задача: ID, владелец/агент, зависимости, источники, тесты, критерии приемки, статус.
8. Изменения main и публикация — только по разрешению пользователя.
9. Новые задачи и дефекты сразу в roadmap; завершенные — `[x]` с датой и ссылкой на коммит.
10. Установленные сетевые правила и привилегии должны быть минимальны и обратимы.

## 17. Приоритет MVP
**M0:** репозиторий + AGENTS.md + worktree + CI.
**M1:** Windows UI и настройки.
**M2:** импорт/выбор/подключение через sing-box.
**M3:** Windows TUN + служба + Kill Switch + DNS/IPv6 + надежность.
**M4:** installer/update/release и тесты. Только после M4 заявлять полноценный Windows MVP.

## 18. Шаблон задачи для агентов
```
ID:
Цель:
Платформа/модуль:
Зависимости:
Ссылки SmartBase/PRIVATE GATEWAY/Hiddify:
Лицензионное решение:
План реализации:
Тесты:
Критерии приемки:
Worktree/ветка:
Статус: [ ] / [x]
Коммит/дата:
```
