# Реестр заимствований

Каждый перенос или изучение чужого кода — строка здесь до коммита: задача → источник (URL файла
и коммит) → лицензия → что переносим или пишем заново → тесты → решение.

## SmartBase — инженерная база

Репозиторий `rvburov/SmartBase`, коммит `f8592fb125b287f11146ae25a6945e857a44fd85`
(23.10.2026), лицензия **Apache-2.0**. Тот же автор, что и у этого проекта.

| Задача | Файл источника | Что сделано | Проверка | Решение |
| --- | --- | --- | --- | --- |
| Правила агентов | `AGENTS.md` | адаптирован: ветка `master`, слои и запрет сети, общие ресурсы VPN (служба, TUN, брандмауэр), секреты VPN; убраны правила баз данных, расширений и имен сторонних программ | — | перенесено с правкой |
| Рабочие папки агентов | `Scripts/worktree.sh` | перенесен в `scripts/worktree.sh`: основная ветка определяется (`PG_BASE_BRANCH`, `main` или `master`), убраны упоминания SmartBase.csproj | ручной прогон двух рабочих папок во временном клоне (этап 0) | перенесено с правкой |
| Автор коммитов в облаке | `Scripts/cloud-session.sh`, `.claude/settings.json` | перенесены; добавлена подсказка про установку .NET пакетом ОС | — | перенесено с правкой |
| Вывод сборки и кеш пакетов | `Directory.Build.props`, `nuget.config` | идея перенесена: artifacts/, кеш в packages/, кеш основной папки для рабочих; добавлены общие свойства сборки | сборка | перенесено с правкой |
| Запрет сетевых вызовов | `BannedSymbols.txt` | идея перенесена (BannedApiAnalyzers), список свой: `desktop/BannedSymbols.NoNetwork.txt` для слоев без сети | мутант: HttpClient в Core → ошибка RS0030 | переписано |
| Инструкция в окне образа macOS | `Scripts/github-release/dmg-background.swift`, шаг «Упаковка — macOS» в `.github/workflows/release.yml` (коммит `13da1bbf34a8ce8c83473d2125892ac6fadd1714`, 05.11.2026) | переписано: `scripts/dmg-background.swift` — фон окна рисуется кодом, цвета из `design-tokens.json`, шаги RU и строка EN, стрелка «перетащить в Программы»; в `scripts/package-macos.sh` — образ в два приема (UDRW → UDZO), оформление окна Finder через AppleScript без падения сборки, контрольное монтирование | контрольное монтирование в `package-macos.sh` (фон, программа, ярлык, подпись), `publish.yml` на macOS | перенесено с изменениями (решение пользователя 2026-11-05) |
| Выпуск на GitHub | `Scripts/github-release/release.sh`, `check-version.sh`, `release-notes.sh`, `release-test.sh`, `release-history.md`, шаг выпуска в `.github/workflows/release.yml` (коммит `8963e3a95baa4d2ca07c863ae36a1d41099023f8`, 09.11.2026) | переписаны под проект (решение пользователя 2026-11-10): `scripts/release.sh` — те же проверки, план, «Выпустить? [y/N]», коммит `release: vX.Y.Z`, аннотированный тег и отправка; версия — `Directory.Build.props` (может равняться отладочной) и больше прошлого тега, тесты — `desktop/tests/Unit`; `scripts/check-version.sh` — `Directory.Build.props` и `CHANGELOG.md` (бейджа версии нет); `scripts/release-notes.sh` — шаблон «Что нового → Установка → Первый запуск → Обновление → Сверка» для MSI, `.dmg`, `.deb`, `.rpm` и APK, «@слово» — кодом, строки склеены, файлы сверяются с папкой выпуска; история — `CHANGELOG.md` в формате `release-history.md`; повтор workflow обновляет готовый выпуск (`gh release edit`) | `scripts/tests/release-test.sh` на своем стенде с локальным origin (по образцу SmartBase), мутанты: без кода для «@», без даты, без проверки секции | перенесено с правкой |

## Hiddify — исследовательский образец

`hiddify/hiddify-app`, лицензия с дополнительными условиями (форк, публикация исходников,
некоммерческое использование, атрибуция). **Код не копируется** до отдельной юридической
проверки. Изучаются только идеи интерфейса и документированные форматы.

| Задача | Что изучено | Решение |
| --- | --- | --- |
| Макет интерфейса | общая компоновка «компактный главный экран + подробные настройки» по скриншоту пользователя | свой макет v1.0 (`docs/design`), код и разметка не заимствованы |

## PRIVATE GATEWAY — форматы

`rvburov/private-gateway`, коммит `883e6f7` (25.10.2026), проект того же автора. Источник форматов
ссылок и подписок для тестов совместимости. Секреты, ключи, реальные адреса подписок, UUID устройств
и учетные данные не переносятся.

| Задача | Файл источника | Что сделано | Проверка | Решение |
| --- | --- | --- | --- | --- |
| Эталоны профиля устройства | `internal/xray/config.go` (`ClientProfiles`, `Profile`), `internal/httpapi/subscription.go` | вывод `ClientProfiles` на заведомо ненастоящих значениях (адреса из документации, UUID `11111111-…`, ключ из нулей) записан эталонами `shared/testdata/subscriptions/private-gateway` и `links/vless/private-gateway*`: ссылка, base64, sing-box, Mihomo, IPv6; код сервера не переносится | `LinkParserTests` | перенесены данные |
| Сервер для теста совместимости | `internal/xray/config.go` (`Render`) | форма конфига сервера (VLESS + REALITY + Vision на raw) повторена в тесте как JSON; ключ REALITY — тестовый | `PrivateGatewayCompatibilityTests` | повторен формат |
| Название подписки | `internal/httpapi/subscription.go` | заголовок `Profile-Title: base64:…` читается как название группы | `SubscriptionDownloaderTests`, `ImportPageTests` | написано заново |

Уточнение 2026-11-02: проверены файлы
[subscription.go](https://github.com/rvburov/private-gateway/blob/724b12299bdf964f34cc68b9c0296448ce802438/internal/httpapi/subscription.go)
и [config.go](https://github.com/rvburov/private-gateway/blob/724b12299bdf964f34cc68b9c0296448ce802438/internal/xray/config.go)
коммита `724b12299bdf964f34cc68b9c0296448ce802438`. `subscriptionURIs` отдает общий список XHTTP первым и TCP Vision
вторым при включенном XHTTP; известным legacy-клиентам остается TCP. Выбор зависит от `User-Agent`, адрес подписки
один. У двух транспортов отдельные порт, ключ REALITY и short ID, общий UUID; Vision задается только TCP.
Лицензия в корне этого коммита не объявлена: исходный Go-код не копируется и не переводится. Использованы факты
формата и поведения для объяснения интерфейса и собственной регрессии импорта на ненастоящих данных
(`ImportPageTests.Tcp_vision_and_xhttp_from_one_address_remain_two_profiles_in_one_subscription`). Доступность реального
порта и текущее содержимое секретной подписки этим чтением не проверялись.

## WireGuard для Windows — справочник по WFP

`WireGuard/wireguard-windows`, коммит `6ece77bc487c8aa697e3c092197621c4f3e5ccb8` (25.10.2026),
лицензия **MIT**.

| Задача | Файл источника | Что сделано | Проверка | Решение |
| --- | --- | --- | --- | --- |
| Блок при обрыве на Windows (WFP) | `tunnel/firewall/types_windows.go`, `types_windows_64.go`, `rules.go` | сверены GUID слоев и условий WFP, коды действий и типов значений, смещения FWPM_FILTER0 и FWPM_FILTER_CONDITION0 на 64-битной Windows — это факты API Windows (fwpmu.h, fwpmtypes.h); `WfpRules` и `WfpKillSwitch` написаны заново: своя политика, не динамическая сессия, ключи фильтров по порядку; код не переносится | `WfpKillSwitchTests` (правила на всех ОС, фильтры — в CI на Windows) | изучен как справочник |

| Пересылка на Windows (IPFORWARD) | microsoft/windows-rs `metadata/win32/fwpmu.rdl`@85d27ee4 (MIT/Apache-2.0); simplewall `src/wfp.c`@bfdc14de (GPL-3.0); Mullvad `docs/known-issues.md` (GPL-3.0) — прочитаны при разборе пункта | сверены GUID слоев FWPM_LAYER_IPFORWARD_V4/V6 и условия IP_DESTINATION_ADDRESS, флаги pass-through (fwptypes); у simplewall взята идея пропускать pass-through на слое пересылки, у Mullvad — что WSL за NAT может идти мимо слоев IP. Код не переносится, `WfpRules` и `WfpKillSwitch` дописаны сами | точный список правил на всех ОС; настоящие фильтры на Windows в CI; проба с контейнером за WinNAT (2026-10-25: слои пересылки его не видят) | идеи и факты API, код свой |

| ВМ без туннеля (брандмауэр Hyper-V) | Mullvad `mullvadvpn-app/talpid-core/src/firewall/windows/hyperv.rs`, ветка main на 25.10.2026 (GPL-3.0) | факты API: пространство WMI `root\standardcimv2`, класс `MSFT_NetFirewallHyperVRule`, свойства ElementName, InstanceID, Action = 4 (блок), Enabled = 1, Direction 1/2, удаление по InstanceID, WBEM_E_NOT_FOUND — не ошибка. Код (Rust, крейт wmi) не переносится: `HyperVFirewall` написан заново на System.Management, свои InstanceID и названия | тест в CI на Windows (ставит, снимает, сверка при запуске службы) | факты API, код свой |

## Ядра и библиотеки

| Компонент | Лицензия | Как используется |
| --- | --- | --- |
| Xray-core | MPL-2.0 | готовые сборки, отдельный процесс (десктоп); libXray на мобильных. Конфиг (`XrayConfigBuilder`) написан заново по документированному формату конфигурации Xray (xtls.github.io), код ядра не переносится |
| sing-box | GPL-3.0 | TUN режима VPN (и кандидат для Hysteria2/TUIC): готовые сборки выпуска `SagerNet/sing-box` тега из `shared/cores/sing-box.version` (v1.14.3), SHA-256 закреплены в `shared/cores/sing-box.sha256`; только отдельным процессом службы, конфиг — через stdin. Конфиг (`SingBoxConfigBuilder`) написан заново по документированному формату (sing-box.sagernet.org), код sing-box не переносится и не встраивается. Сборка распространяется с `LICENSE`; в выпуск программы — ссылка на исходники того же тега (GPL-3.0, раздел 6) |
| sing-box для macOS 13 | [исходники `v1.14.3`, коммит `7054cac5124465ffdb145870d7c01885566711b1`](https://github.com/SagerNet/sing-box/tree/7054cac5124465ffdb145870d7c01885566711b1), [GPL-3.0 LICENSE](https://github.com/SagerNet/sing-box/blob/7054cac5124465ffdb145870d7c01885566711b1/LICENSE), [параметры сборки](https://github.com/SagerNet/sing-box/blob/7054cac5124465ffdb145870d7c01885566711b1/Makefile) | Отдельный процесс той же закрепленной версии, нативная сборка на Mac с CGO и deployment target 13.0 вместо готового архива, который требует macOS 26. Тег сверяется с `sing-box.commit`; зависимости по исходным `go.mod`/`go.sum`, `-mod=readonly`, `go mod verify`. Используются upstream `release/DEFAULT_BUILD_TAGS_OTHERS` и `release/LDFLAGS`: gvisor, QUIC/Hysteria2/TUIC, uTLS и остальные возможности этого набора сохранены; Naive/Cronet клиент не использует. `LICENSE`, исходный коммит и параметры сопровождают бинарник. Код ядра не изменяется и не встраивается. Проверки: контракт скрипта на подмененных git/go, общий Mach-O guard, нативный пакет и VPN/TUN в CI |
| Геобаза сайтов Xray (`geosite.dat`) | v2fly/domain-list-community — MIT (https://github.com/v2fly/domain-list-community, коммит `fa3cb96fbc210b8d6466251a1d71b4a6e63a9c85` от 26.10.2026, `shared/cores/geosite.version`) | с 26.10.2026 выпуск возит свою сборку: `scripts/get-geosite.sh` берет закрепленный коммит и собирает `geosite.dat` генератором этого же репозитория (go, модули по его `go.sum`) — в выпуск рядом с ядром (`cores/xray`), правила «geosite:категория» в «Маршрутизации»; `release-smoke.sh` проверяет, что Xray читает `geosite:category-ads-all`. Прежний источник — сборка Loyalsoldier/v2ray-rules-dat из архива Xray (GPL-3.0, с данными других списков) — остается только для тестов и разработки (решение 361). `geoip.dat` (GeoIP из данных MaxMind GeoLite2) не возится с 26.10.2026: категорию private заменил список `XrayConfigBuilder.LocalNetworks` — тот же 21 диапазон, что в геобазе закрепленной версии (факты, не код): `plugin/special/private.go` Loyalsoldier/geoip@883184c и v2fly/geoip |
| Avalonia | MIT | интерфейс десктопа |
| Inter, JetBrains Mono | OFL-1.1 | шрифты десктопа, встроены |
| CommunityToolkit.Mvvm | MIT | MVVM |
| Google.Protobuf, Grpc.* | BSD-3-Clause, Apache-2.0 | контракт IPC |
| System.Management | MIT | WMI на Windows: правила брандмауэра Hyper-V для ВМ без туннеля |
| xUnit v3, Shouldly | Apache-2.0, BSD-3-Clause | тесты |
| YamlDotNet | MIT | разбор YAML Clash/Mihomo — только события разборщика (`YamlTree`) |
| ZXing.Net | Apache-2.0 | QR-код: распознавание картинки при импорте и рисование для «Поделиться» |
| Tmds.DBus.Protocol | MIT | уведомления Linux по D-Bus (`FreedesktopNotifier`); пакет и так приходит с Avalonia.FreeDesktop — та же версия 0.94.1, код не переносится |
| libXray (XTLS) | MIT | ядро Android: `XTLS/libXray`, коммит `756ef80f41903eaf7bbeeb38a0a1f802ec204464` (23.10.2026, `android/libxray.version`), .aar собирает `scripts/get-libxray.sh` (gomobile) в CI; код не переносится — вызываются `LibXray.invoke` (apiVersion 3), `registerDialerController`, `setDNS`/`resetDNS` по README и `android_wrapper.go` этого коммита; внутри — Xray-core (MPL-2.0) версии из его go.mod |
| snakeyaml-engine | Apache-2.0 | разбор YAML Clash/Mihomo на Android (`YamlTree` общего модуля) — только события разборщика, с пределами глубины и числа узлов, как у десктопа |
| ZXing (`com.google.zxing:core` 3.5.4) | Apache-2.0 | QR-код на Android (`QrCode` общего модуля): модули для «Поделиться», распознавание картинки и кадра камеры — тот же проект, что ZXing.Net десктопа; только вычисления, код не переносится |
| AndroidX, Jetpack Compose, Room, DataStore, WorkManager, CameraX, Hilt (Dagger), Kotlin, kotlinx.coroutines | Apache-2.0 | приложение Android: библиотеки из Google Maven и Maven Central, версии — `android/gradle/libs.versions.toml`; код не переносится |
| Robolectric, JUnit | MIT, EPL-1.0/EPL-2.0 | тесты Android без устройства — только в тестах, в приложение не входят |

## WinINet: прокси текущего пользователя Windows — 2026-11-01

| Задача | Проверенный источник | Лицензия | Что сделано | Проверка | Решение |
| --- | --- | --- | --- | --- | --- |
| Применение и возврат системного прокси без прямой записи HKCU | [Setting and Retrieving Internet Options](https://github.com/MicrosoftDocs/win32/blob/ca8274decead49403d70ceeed4af8d4ca74eaf11/desktop-src/WinInet/setting-and-retrieving-internet-options.md), MicrosoftDocs/win32 `ca8274decead49403d70ceeed4af8d4ca74eaf11` | Документация CC-BY-4.0, примеры MIT; LICENSE и LICENSE-CODE проверены | Собственная C#-реализация `CurrentUserWindowsProxy`: query/set одного PER_CONN_OPTION_LIST с flags/server/bypass/PAC, освобождение памяти по ABI. C++-пример не переносился и не переводился построчно; новой зависимости нет | Подмена backend в Unit, снимки и переходы; Windows native Read → Restore того же снимка → Read добавлен в Network и требует Windows | Использовано описание API, код написан заново |
| Флаги и память INTERNET_PER_CONN_OPTIONW | [INTERNET_PER_CONN_OPTIONW](https://github.com/MicrosoftDocs/sdk-api/blob/656e9656bfaef34dd29cd7ee9e1146aec17e924c/sdk-api-src/content/wininet/ns-wininet-internet_per_conn_optionw.md), MicrosoftDocs/sdk-api `656e9656bfaef34dd29cd7ee9e1146aec17e924c` | Документация CC-BY-4.0, примеры MIT; LICENSE и LICENSE-CODE проверены | Собственные C#-структуры по описанию ABI: FLAGS_UI для чтения намерения, FLAGS для записи, строки результата query освобождаются GlobalFree. Из внешнего репозитория исходники не переносились | Исходники закреплены и сопоставлены с прочитанными документами; реальные WinINet-вызовы в облачном Linux не выполнялись | Использовано описание API |

## Xray: HTTP к FakeDNS в совместном режиме — 2026-11-01

| Задача | Проверенный источник | Лицензия | Что сделано | Проверка | Решение |
| --- | --- | --- | --- | --- | --- |
| Общая скорость на главной: клиент StatsService ядра Xray в службе | XTLS/Xray-core v26.10.21, коммит `1b1647cd9ddf0abc34b981d4611fc03bcac1ca74`: [app/stats/command/command.proto](https://github.com/XTLS/Xray-core/blob/1b1647cd9ddf0abc34b981d4611fc03bcac1ca74/app/stats/command/command.proto) — файл прочитан 2026-11-06 | MPL-2.0 | Пишем заново: `desktop/src/Client.Service/Cores/XrayStats.proto` — только служба `StatsService`, метод `QueryStats` и сообщения `QueryStatsRequest`, `Stat`, `QueryStatsResponse` с теми же именем пакета и номерами полей (это интерфейс ядра, без них оно не ответит); текст, комментарии и остальные методы не переносятся | `GatewayTrafficTests.Real_core_counts_the_bytes_of_its_inbound_but_not_of_the_probe` — настоящий Xray отвечает этому описанию | Принято (решение 579) |
| Причина потери sniffing обычным HTTP и ограничения внутреннего loopback | XTLS/Xray-core v26.10.21, коммит `1b1647cd9ddf0abc34b981d4611fc03bcac1ca74`: [proxy/http/server.go](https://github.com/XTLS/Xray-core/blob/1b1647cd9ddf0abc34b981d4611fc03bcac1ca74/proxy/http/server.go), [proxy/loopback/loopback.go](https://github.com/XTLS/Xray-core/blob/1b1647cd9ddf0abc34b981d4611fc03bcac1ca74/proxy/loopback/loopback.go) | MPL-2.0, LICENSE проверен | Изучены замена session.Content в handlePlainHTTP и SkipDNSResolve у loopback. Go-код не переносился, ядро не менялось. Собственный JSON-конфиг направляет только fake IP дополнительного входа в существующий Shadowsocks-вход TUN | Настоящие DNS и HTTP/SOCKS того же Xray на localhost, правила имен/IP и неизвестный fake IP; отдельный Windows-тест сохраняет реальный TUN | Решение 524: сохранить восстановление имени и обычную маршрутизацию, не ослаблять тест |

## Xray: Vision, фрагментация и Linux splice — 2026-11-02

Проверены XTLS/Xray-core v26.10.21, коммит `1b1647cd9ddf0abc34b981d4611fc03bcac1ca74`,
[LICENSE](https://github.com/XTLS/Xray-core/blob/1b1647cd9ddf0abc34b981d4611fc03bcac1ca74/LICENSE) (MPL-2.0),
[proxy/freedom/freedom.go](https://github.com/XTLS/Xray-core/blob/1b1647cd9ddf0abc34b981d4611fc03bcac1ca74/proxy/freedom/freedom.go),
[proxy/proxy.go](https://github.com/XTLS/Xray-core/blob/1b1647cd9ddf0abc34b981d4611fc03bcac1ca74/proxy/proxy.go)
и [common/platform/platform.go](https://github.com/XTLS/Xray-core/blob/1b1647cd9ddf0abc34b981d4611fc03bcac1ca74/common/platform/platform.go).
Изучены возможность raw splice в freedom цепочки dialerProxy и официальный выключатель `xray.buf.splice`;
каноническое имя имеет приоритет над `XRAY_BUF_SPLICE`. Go-код не копировался и не переводился.

Собственный `CoreProcess.StartXray` задает оба имени со значением `disable` только в окружении дочернего процесса
на Linux, когда конфиг содержит Vision, дозванивающийся через freedom с fragment. Окружение приложения,
другие конфиги и Windows/macOS не меняются. TLS-фрагментация, REALITY и проверка сертификатов сохраняются.
Регрессия — `PrivateGatewayCompatibilityTests.Https`: прямой HTTPS-контроль с точным сертификатом, TCP Vision/XHTTP,
прокси/VPN-входы и включенная/выключенная фрагментация, TLS 1.3 на Linux и отдельный TLS 1.2. Первые неудачные
стенды без исправного прямого HTTPS не считались доказательством дефекта приложения.

## 08.11.2026 — системный DNS macOS для TUN

Задача: устранить привязку DNS туннеля к физическому интерфейсу основной сети.
Прочитаны исходники Apple configd, коммит `585b7f2fca293f4642d21d15c5daf187f63c4796`:
[ip_plugin.c](https://github.com/apple-oss-distributions/configd/blob/585b7f2fca293f4642d21d15c5daf187f63c4796/Plugins/IPMonitor/ip_plugin.c),
[dns-configuration.c](https://github.com/apple-oss-distributions/configd/blob/585b7f2fca293f4642d21d15c5daf187f63c4796/Plugins/IPMonitor/dns-configuration.c).
Лицензия исходников — APSL-2.0. Код не переносится: собственная реализация использует формат динамического
хранилища (дополнительный DNS с `SupplementalMatchDomains` и `SupplementalMatchOrders`, без InterfaceName).
Проверка: прежняя реализация воспроизвела таймаут системного резолвера на обоих стеках TUN в CI
`42414978050`; отдельный UDP-запрос и HTTP по IP там же проходили. Повторный тест проверяет имя через
резолвер ОС, HTTP по имени и восстановление записей. Решение: собственная DNS-запись, без подмены DNS Wi-Fi.

## 08.11.2026 — страны IP и флаги

- Данные: [DB-IP Country Lite, ноябрь 2026](https://db-ip.com/db/download/ip-to-country-lite),
  [CSV](https://download.db-ip.com/free/dbip-country-lite-2026-11.csv.gz), CC-BY-4.0.
  Страница лицензии прочитана: разрешены приложение и преобразование с указанием DB-IP.com.
  Переносим диапазоны IPv4/IPv6 и ISO-код, преобразуем в собственный бинарный формат;
  это оценка геобазы, не гарантия физического расположения или страны выхода через другой прокси.
- Изображения: lipis/flag-icons, коммит `ac7e0eb3259eb62aa6100c435607e2fe03896a97`,
  [flags/1x1](https://github.com/lipis/flag-icons/tree/ac7e0eb3259eb62aa6100c435607e2fe03896a97/flags/1x1),
  [LICENSE](https://github.com/lipis/flag-icons/blob/ac7e0eb3259eb62aa6100c435607e2fe03896a97/LICENSE), MIT,
  Copyright (c) 2013 Panayiotis Lipiridis. Лицензия прочитана; квадратные SVG конвертируем в PNG.
  Земля — собственный рисунок из согласованного макета. Код Hiddify не переносится.
- Проверки: границы диапазонов, IPv4/IPv6, неизвестный адрес, доменные профили,
  смена и скрытие IP, наличие изображений и лицензий.

## 14.11.2026 — возврат исходных квадратных флагов

- Задача: вернуть прежний вид по уточнению пользователя. На экране MacBook исходные флаги отображались нормально;
  жалоба относилась к внешнему монитору. Предыдущее объяснение через пропорции ресурса не подтвердилось.
- Источник: [flag-icons/flags/1x1](https://github.com/lipis/flag-icons/tree/ac7e0eb3259eb62aa6100c435607e2fe03896a97/flags/1x1),
  закрепленный коммит и архив с SHA-256 из `shared/countries/sources.json`, MIT. Нового стороннего кода нет.
- Возвращены байты SVG и PNG из коммита `bd7463b`: квадратные ресурсы 112×112 и рисунок с рамкой 28×28.
  Генератор снова выбирает вариант 1:1. Новых зависимостей приложения нет; собственная Земля не меняется.
- Проверки: все ресурсы и пропорции, неизвестная страна, смена страны, одинаковые PNG в приложении и макете,
  снимки США, Японии, Нидерландов и Земли. Исходная геобаза не меняется.

## 14.11.2026 — восстановление подтверждения установки Android

- Задача: после завершения процесса восстановить собственную сессию APK и заново получить подтверждение Android.
- Проверен контракт `PackageInstallerSession.commit` в AOSP:
  [Android 9, 00d9027e38e8337b2358a06aa7e14840d6a1dfe9](https://android.googlesource.com/platform/frameworks/base/+/00d9027e38e8337b2358a06aa7e14840d6a1dfe9/services/core/java/com/android/server/pm/PackageInstallerSession.java),
  [Android 16, 99b01a65cc4c104933788b3143285ab6bae65827](https://android.googlesource.com/platform/frameworks/base/+/99b01a65cc4c104933788b3143285ab6bae65827/services/core/java/com/android/server/pm/PackageInstallerSession.java).
  В заголовке прочитана лицензия Apache-2.0. Исходный код не копируется и не переводится: изучено поведение публичного API.
- Собственная реализация записывает фазу и номер до передачи APK, сверяет `mySessions` и повторяет `commit` только
  для своего уже переданного пакета с новым приемником результата. Новый callback приносит новый системный Intent;
  недописанная сессия и пропавшая без установки версия требуют повторной попытки.
- Регрессии: холодный callback, смерть процесса до commit и после запроса подтверждения, повторный запрос Android
  без новой загрузки, старые/повторные callback, ошибка записи. Проверка принадлежности сессий — Robolectric API 28/31/35;
  настоящее системное подтверждение и ограничения прошивки — отдельная проверка на телефоне.

## 14.11.2026 — согласование масштаба окна после смены монитора

- Изучена Avalonia 12.1.4, коммит `2d79ba55fae1b1e71c69f93a668080c9029e6a31`,
  [лицензия MIT](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/licence.md).
- Источники: [AvnView.mm](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/native/Avalonia.Native/src/OSX/AvnView.mm),
  [metal.mm](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/native/Avalonia.Native/src/OSX/metal.mm),
  [CompositingRenderer.cs](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Rendering/Composition/CompositingRenderer.cs),
  [IMacOSTopLevelPlatformHandle.cs](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Platform/IMacOSTopLevelPlatformHandle.cs).
  Открытая [issue 22684](https://github.com/AvaloniaUI/Avalonia/issues/22684) описывает совпадающий симптом: managed DPI
  уже правильный, нативная поверхность после смены экранов увеличена; это свидетельство сходства, не доказательство на Mac пользователя.
- Код не перенесен. Собственная связка вызывает публичные методы AppKit у текущего NSView, полученного через API Avalonia:
  обновление backing properties и немедленное отображение слоя применяют ожидающие размеры Metal на главном потоке.
  События объединяются; обратное уведомление масштаба не порождает бесконечную очередь. Версия Avalonia не меняется.
- Проверки: headless DPI и жизненный цикл; настоящий Metal drawable в `--check-display`, мутант без нативного восстановления;
  физическое отключение монитора и пробуждение — ручной сценарий `docs/testing/display-scaling.md`.

## 14.11.2026 — подтвержденное снятие собственных фильтров WFP

- Проверена документация MicrosoftDocs/sdk-api, коммит `656e9656bfaef34dd29cd7ee9e1146aec17e924c`:
  [FwpmFilterCreateEnumHandle0](https://github.com/MicrosoftDocs/sdk-api/blob/656e9656bfaef34dd29cd7ee9e1146aec17e924c/sdk-api-src/content/fwpmu/nf-fwpmu-fwpmfiltercreateenumhandle0.md),
  [FwpmFilterEnum0](https://github.com/MicrosoftDocs/sdk-api/blob/656e9656bfaef34dd29cd7ee9e1146aec17e924c/sdk-api-src/content/fwpmu/nf-fwpmu-fwpmfilterenum0.md),
  [FwpmFilterDestroyEnumHandle0](https://github.com/MicrosoftDocs/sdk-api/blob/656e9656bfaef34dd29cd7ee9e1146aec17e924c/sdk-api-src/content/fwpmu/nf-fwpmu-fwpmfilterdestroyenumhandle0.md),
  [FwpmTransactionBegin0](https://github.com/MicrosoftDocs/sdk-api/blob/656e9656bfaef34dd29cd7ee9e1146aec17e924c/sdk-api-src/content/fwpmu/nf-fwpmu-fwpmtransactionbegin0.md).
  Лицензии этого закрепленного источника проверены выше: документация CC-BY-4.0, примеры MIT.
- Использовано описание API: снимок перечисления создается и читается в одной транзакции; страницы освобождаются
  через `FwpmFreeMemory0`, дескриптор — через `FwpmFilterDestroyEnumHandle0`. Число меньше запрошенного завершает обход.
  Перечисление возвращает только доступные для чтения фильтры, поэтому ошибка удаления подслоя/провайдера, в том числе
  `FWP_E_IN_USE`, остается ошибкой восстановления сети.
- Код написан самостоятельно. Удаляются только фильтры с точным совпадением собственного провайдера **и** подслоя.
  Нет остановки по первому отсутствующему ключу или по 32 пропускам; правила других программ не меняются.
- Проверки: отбор чужих и своих записей на нескольких страницах, ошибка следующей страницы и ошибки удаления; существующая
  нативная проверка Windows дополнена удалением первого фильтра и первых 40 ключей перед полным снятием.

## 14.11.2026 — время жизни PNG-снимка Headless

- Изучена Avalonia 12.1.4, коммит `2d79ba55fae1b1e71c69f93a668080c9029e6a31`, MIT (лицензия проверена выше):
  [WriteableBitmapImpl.cs](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Skia/Avalonia.Skia/WriteableBitmapImpl.cs),
  [ImageSavingHelper.cs](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Skia/Avalonia.Skia/Helpers/ImageSavingHelper.cs),
  [HeadlessWindowImpl.cs](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Headless/Avalonia.Headless/HeadlessWindowImpl.cs).
- `WriteableBitmapImpl.GetSnapshot` передает указатель `_bitmap.GetPixels()` в `SKImage.FromPixels`; сохранение PNG
  кодирует эти пиксели. Временный результат `CaptureRenderedFrame()?.Save(...)` не задает время жизни владельца буфера.
  В Windows CI стек `0xC0000005` заканчивался в `sk_pngencoder_encode` при снимке DPI 2.
- Код не перенесен. Только тестовые помощники явно удерживают bitmap через `using` и `GC.KeepAlive` до завершения Save,
  затем освобождают его. Проверка сохраняет настоящие PNG при параллельном GC и декодирует их; снимок и DPI-проверки не убраны.

## 15.11.2026 — управляемые стеки диагностического Mac-прогона

- Задача: определить этап ожидания длительного теста перед выпуском, не выгружая память процесса и не меняя
  приложение или обязательные выпускные гейты.
- Инструмент CI — `dotnet-stack` 10.0.750501 из Microsoft dotnet/diagnostics; версия и repository commit
  `ad724c4b85e7ae775e42f393c6e95a7557a57954` проверены по
  [NuGet metadata](https://api.nuget.org/v3-flatcontainer/dotnet-stack/10.0.750501/dotnet-stack.nuspec).
- Лицензия — MIT, .NET Foundation and Contributors, проверена в
  [LICENSE.TXT](https://raw.githubusercontent.com/dotnet/diagnostics/ad724c4b85e7ae775e42f393c6e95a7557a57954/LICENSE.TXT).
  Исходники инструмента не копируются и в приложение он не включается.
- Python-наблюдатель и тестовые отметки этапов написаны для проекта. Проверки: штатный выход дочернего процесса,
  сохранение ошибки, зависание, прекращение дерева процессов и вызов сборщика стеков; диагностический короткий
  запуск не считается выпускной приемкой. В артефактах только статические этапы и управляемые стеки, без дампов памяти.

## 19.11.2026 — списки доменов сервисов («Все адреса сервиса»)

- Задача: флажок «Все адреса YouTube» в листе правила (решение 741) — правило для всех адресов сервиса, на телефонах без
  базы geosite.
- Данные: v2fly/domain-list-community, коммит `fa3cb96fbc210b8d6466251a1d71b4a6e63a9c85` (тот же, что
  `shared/cores/geosite.version` и `geosite.dat` десктопа), файлы
  [data/youtube и другие](https://github.com/v2fly/domain-list-community/tree/fa3cb96fbc210b8d6466251a1d71b4a6e63a9c85/data),
  [LICENSE](https://github.com/v2fly/domain-list-community/blob/fa3cb96fbc210b8d6466251a1d71b4a6e63a9c85/LICENSE), MIT,
  Copyright (c) 2018-2019 V2Ray. Лицензия прочитана.
- Переносим только данные — домены и «full:» тринадцати сервисов с вложенными «include:», без «regexp:», «keyword:», «@ads»
  и «@cn» — в `shared/specifications/routing/services.json`; выбор и обработку делает свой `scripts/gen-services.cs`, код
  генератора репозитория не копируется. Строки в `shared/licenses/android.json` и `ios.json`; у десктопа источник уже
  указан строкой `geosite.dat` в `components.json`.
- Проверки: десктоп — коммит списка равен `geosite.version`, имена годятся в шаблон правила; Android и iOS — список в коде
  и ресурсах совпадает с общим файлом, ядро телефона получает домены сервиса.
