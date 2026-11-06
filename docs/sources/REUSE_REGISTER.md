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
