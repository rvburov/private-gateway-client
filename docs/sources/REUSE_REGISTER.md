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
| Геобаза сайтов Xray (`geosite.dat`) | сборка Loyalsoldier/v2ray-rules-dat — GPL-3.0; основа — v2fly/domain-list-community (MIT) и другие списки | файл из архива выпуска Xray, кладется в выпуск рядом с ядром (`cores/xray`) как есть: правила «geosite:категория» в «Маршрутизации». Источник для закрепленной v25.8.3 — workflow выпуска Xray этого тега (`scheduled-assets-update.yml`): Loyalsoldier/v2ray-rules-dat, ветка `release`. `geoip.dat` (GeoIP из данных MaxMind GeoLite2) не возится с 26.10.2026: категорию private заменил список `XrayConfigBuilder.LocalNetworks` — диапазоны из реестров особых адресов IANA (факты, не код), сверены с `plugin/special/private.go` Loyalsoldier/geoip |
| Avalonia | MIT | интерфейс десктопа |
| Inter, JetBrains Mono | OFL-1.1 | шрифты десктопа, встроены |
| CommunityToolkit.Mvvm | MIT | MVVM |
| Google.Protobuf, Grpc.* | BSD-3-Clause, Apache-2.0 | контракт IPC |
| System.Management | MIT | WMI на Windows: правила брандмауэра Hyper-V для ВМ без туннеля |
| xUnit v3, Shouldly | Apache-2.0, BSD-3-Clause | тесты |
| YamlDotNet | MIT | разбор YAML Clash/Mihomo — только события разборщика (`YamlTree`) |
| ZXing.Net | Apache-2.0 | QR-код: распознавание картинки при импорте и рисование для «Поделиться» |
| Tmds.DBus.Protocol | MIT | уведомления Linux по D-Bus (`FreedesktopNotifier`); пакет и так приходит с Avalonia.FreeDesktop — та же версия 0.94.1, код не переносится |
