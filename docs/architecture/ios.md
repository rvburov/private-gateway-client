# iOS

Приложение iOS (этап 10 ROADMAP): Swift 6, SwiftUI, расширение Network Extension (Packet Tunnel) с ядром libXray,
iOS 16+ (решения в [DECISIONS.md](../roadmap/DECISIONS.md), «Android / iOS»). Аккаунта Apple Developer нет — приложение и
расширение пишутся, собираются и проверяются в CI на симуляторе; туннель на iPhone и TestFlight — когда аккаунт будет.

## Структура

| Часть | Что внутри | Как проверяется |
| --- | --- | --- |
| `ios/Core` — `PrivateGatewayCore` | без UIKit и NetworkExtension: описание сервера (`ServerDefinition`), разбор ссылок (`LinkParser`), подписок и конфигов (`ImportParser`: ссылки, base64, sing-box JSON, Clash/Mihomo YAML), запись ссылки (`LinkWriter`), флаг страны (`CountryFlag`); конфиг Xray (`XrayConfig`, ответ libXray — `XrayResponse`), настройки туннеля и свой DNS (`TunnelSettings`), строгий разбор адресов IP и частные сети (`IpAddress`, `PrivateNetwork`); список серверов — сервер без адреса и ключа (`ServerItem`), задержка (`Latency`), поиск, вкладки, фильтр и порядок (`ServerList`), быстрая смена (`QuickSwitch`), подписи (`ServerFacts`), узнавание того же сервера (`ServerKeys`), свое название (`ServerNames`); состояние туннеля (`TunnelStatus`) | `swift test --package-path ios/Core` — на Linux и macOS (задание CI «iOS — общий модуль») |
| `ios/Core` — `PrivateGatewayTheme` | тема, тексты RU/EN и иконки из `shared/specifications` и `Icons.axaml` десктопа — значения без SwiftUI (`PgColor`, `PgType`, `PgSpace`, `PgTexts`, `PgIcons`, разбор контура `SvgPath`); код генерирует плагин сборки `SharedResources` (исполняемый `SharedGen`, логика — `SharedGenKit`) | тот же `swift test`: каждый цвет и длина — как в Tokens.axaml десктопа, каждый текст — как в JSON, каждая иконка разбирается |
| `ios/App` | приложение SwiftUI: тема SwiftUI из `PrivateGatewayTheme` (`Color`, шрифт, `PgIcon`, карточка, фон), главная по доске 25 — карточка состояния с четырьмя чипами защиты, сервер, кнопка включения внизу | `scripts/ios-test.sh` — тесты XCTest на симуляторе iPhone (задание CI «iOS — приложение (симулятор)», macOS) |
| `ios/PacketTunnel` | расширение туннеля (`NEPacketTunnelProvider`); пока ядра нет, честно отказывает с кодом PGC-SVC-002, а не поднимается без защиты | собирается вместе с приложением; туннель на симуляторе не поднять — проверка на iPhone, когда будет аккаунт |

Общий модуль — перенос `android/core` один к одному: те же правила, те же коды ошибок, тесты на тех же эталонах
`shared/testdata` (ссылки, подписки, «туда и обратно» через `LinkWriter`, один сервер из четырех форматов PRIVATE GATEWAY)
и те же отдельные случаи конфигов (номер строки ошибки, порядок строк, BOM, вложенность и ссылки-«бомбы» YAML).

Конфиг Xray — тот же, что у Android: расширение туннеля собирает его само из описания сервера и настроек (готовый текст
конфига не принимается — модель угроз, как у службы десктопа); вход — TUN с дескриптором в `env` (`xray.tun.fd`), выход
сервера — те же поля, что у десктопа, DNS из туннеля — через сервер, локальная сеть и IPv6 — по настройкам, журнал
соединений выключен. Hysteria2 и TUIC ядро не берет (`unsupportedProtocol`): второго ядра на телефоне нет, а iOS не дает
приложению запускать отдельные процессы. Тесты — те же случаи, что `XrayConfigTest` и `TunnelSettingsTest` Android.

## Устройство разбора

- Текст разбирается по скалярам Unicode с целыми индексами (`Scalars`), а не по `Character`: у Swift «\r\n» и флаг страны —
  один символ, и разрез по «\n» или поиск «#» вели бы себя не так, как на десктопе и Android.
- JSON — свой разбор (`JsonTree`, как у Android): комментарии и запятая перед скобкой конфигов sing-box, число — его текст,
  строки начала элементов списка, глубина не больше 64.
- YAML — libyaml через пакет Yams 5.1.3 (MIT, `shared/licenses/ios.json`), дерево — свое (`YamlTree`): «~», «null» и пустые
  значения без кавычек — нет значения, слияние «<<», не больше 64 уровней и 200 000 узлов (ссылки-«бомбы» считаются при
  обходе, до того как займут память). Разборщик Yams рекурсивен, поэтому до него текст проверяется на глубину скобок «{»/«[»
  (больше 64) и отступ (больше 512 пробелов) — такой конфиг сразу ошибка, а не переполнение стека (решение 455).
- Ошибка разбора конфига — одна строка `bad_encoding` с номером строки, где разбор встал; незакрытая скобка — строка, где она
  открыта (как у десктопа и Android).

## Тема, тексты и иконки

Как у Android (build-logic), код темы не хранится в git, а пишется при каждой сборке модуля `PrivateGatewayTheme`: плагин
SwiftPM `SharedResources` запускает `SharedGen`, тот читает `design-tokens.json`, `localization/ru.json` и `en.json` и
`desktop/src/Client.UI/Themes/Icons.axaml` и пишет `Tokens.swift`, `Texts.swift` и `Icons.swift` в папку сборки. Правка
токенов или текстов попадает в приложение без ручного шага; ошибка в них (разные ключи RU и EN, разные подстановки, два
ключа с одним именем, неразобранный цвет) роняет сборку с объяснением (решение 456).

- Значения — без SwiftUI (`PgRGBA` — ARGB одним числом, `PgTextStyle`, `PgShadowLayer`, длины — `Double` в пунктах), чтобы
  модуль собирался и проверялся на Linux; цвет, шрифт и контур SwiftUI из них собирает приложение. Имена — как у Android:
  `color.bg.window` → `PgColor.Bg.window`, `state.connected.ring` → `PgState.Connected.ring`, `type.h1` → `PgType.h1`.
  `PgTokens.colors` и `PgTokens.lengths` — те же значения по именам ресурсов десктопа: тест сверяет все 75 цветов и 31 длину
  с Tokens.axaml. Прозрачность `rgba(…, 0.70)` считается в десятичной записи с округлением половины вверх — 179, как у
  десктопа и Android, а не 178 из двоичной дроби.
- Тексты — `PgTexts(язык)`: ключ `status.connected.title` → свойство `statusConnectedTitle`, текст с «{N}» → функция с
  аргументами по порядку; `text(key:)` — по ключу, собранному на ходу. Язык — первый русский или английский из
  предпочитаемых языков системы, иначе английский (как values/values-ru у Android); выбор языка для одного приложения в
  «Настройках» iOS работает, когда приложение объявит оба языка в Info.plist.
- Иконки — данные пути SVG (`PgIcons.navHome`), контур — `SvgPath.parse`: M, L, H, V, C, S, Q, T, A, Z, числа без
  разделителей, флаги дуги слитно; дуги переводятся в кривые Безье не больше четверти оборота (SVG 1.1, F.6.5).

## Проект и сборка

Проект Xcode не хранится в git: его, Info.plist и entitlements целей генерирует XcodeGen из `ios/project.yml`
(`scripts/ios-project.sh`; версия 2.46.0 закреплена, сумма архива сверяется, сам XcodeGen лежит в `artifacts/tools` и в
приложение не попадает). `scripts/ios-test.sh` собирает приложение и расширение без подписи и гоняет тесты на первом
доступном симуляторе iPhone самой новой iOS; итог — `artifacts/ios-tests.xcresult`. Нужны macOS и Xcode (решение 457).

- Идентификаторы: приложение — `com.privategateway.client`, расширение — `com.privategateway.client.tunnel`, общая папка
  приложения и расширения — App Group `group.com.privategateway.client`; права — `packet-tunnel-provider`. Без подписи
  права в симуляторе не действуют — они вступят в силу с аккаунтом Apple Developer.
- Swift 6 и строгая проверка многопоточности — во всех целях. Только iPhone и книжная ориентация, как в макете.
- Язык: в приложении есть папки `en.lproj` и `ru.lproj`, поэтому iOS предлагает выбор языка приложения (Настройки →
  Private Gateway → Язык); тексты берутся по `Bundle.main.preferredLocalizations`, затем по языкам iPhone.
- Блок при обрыве на iOS — системный «весь трафик только через VPN» (`includeAllNetworks`): пока туннель поднят или
  поднимается, трафик мимо него не уходит; выключенный туннель блока не держит. Так его и показывает чип на главной.

## Дальше

Ядро libXray для iOS (.xcframework, gomobile) в расширении туннеля, хранение (GRDB, Связка ключей и App Group), остальные
экраны SwiftUI (серверы, карточка сервера, проверка, настройки, островок навигации) и управление туннелем
(`NETunnelProviderManager`).
