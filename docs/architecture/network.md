# Сеть: карта механизмов и исходников

Пользовательское объяснение режимов, примеры и различия ОС — [«Как работает сеть»](../user/network.md).
Этот документ связывает понятия с компонентами реализации. Статус доработок и реальные проверки ОС нужно читать
отдельно от описания конфигурации: построенный JSON и успешный тест модели не подтверждают установленный фильтр.

## Две оси настроек

`ConnectionMode` определяет вход: VPN/TUN либо локальный прокси. `RoutingMode` определяет применение прямых исключений:
`AllTraffic` и `Split`. `RuleAction` задает действие для совпавшего сайта: `Direct`, `Proxy`, `Block`.
`ConnectionSettings.SystemProxy` хранит выбор прокси-режима (по умолчанию включен), `VpnSystemProxy` — отдельный
выбор совместной работы с TUN (по умолчанию выключен, требуется явное включение). Применение настройки прокси ОС
не заменяет маршрутизацию TUN. Разделение сохраняет поведение старых настроек при обновлении.

| Механизм | Где смотреть |
| --- | --- |
| Настройки входа и системного прокси | [ConnectionSettings.cs](../../desktop/src/Client.Core/Settings/ConnectionSettings.cs) |
| Весь трафик, раздельный режим, правила | [RoutingSettings.cs](../../desktop/src/Client.Core/Settings/RoutingSettings.cs) |
| DNS, IPv6, локальная сеть, строгий режим | [ProtectionSettings.cs](../../desktop/src/Client.Core/Settings/ProtectionSettings.cs) |
| Конфиг Xray, входы и правила сайтов | [XrayConfigBuilder.cs](../../desktop/src/Client.Network/Xray/XrayConfigBuilder.cs) |
| TUN sing-box, прямые процессы и транспортный endpoint | [SingBoxConfigBuilder.cs](../../desktop/src/Client.Network/SingBox/SingBoxConfigBuilder.cs) |
| Запуск, подсеть и остановка TUN | [VpnTunnel.cs](../../desktop/src/Client.Service/Cores/VpnTunnel.cs) |
| Подключение и состояние службы | [Gateway.cs](../../desktop/src/Client.Service/Gateway.cs) |
| Пробы связи и переподключение | [Gateway.Reconnect.cs](../../desktop/src/Client.Service/Gateway.Reconnect.cs) |
| Проверка профиля до первого VPN из открытой сети | [Gateway.Preflight.cs](../../desktop/src/Client.Service/Gateway.Preflight.cs), [ConnectionPreflight.cs](../../desktop/src/Client.Service/Cores/ConnectionPreflight.cs) |
| Измерения DNS, IPv6 и IP | [Gateway.Leaks.cs](../../desktop/src/Client.Service/Gateway.Leaks.cs) |
| Пользовательское управление и статусы прокси | [IUserProxyController.cs](../../desktop/src/Client.Application/Connection/IUserProxyController.cs), [UserProxyController.cs](../../desktop/src/Client.Infrastructure/Service/UserProxyController.cs) |
| Владение снимком и возврат после сбоя | [UserProxyLease.cs](../../desktop/src/Client.Infrastructure/Service/UserProxyLease.cs) |
| WinINet/HKCU и флаги PAC/WPAD | [CurrentUserWindowsProxy.cs](../../desktop/src/Client.Infrastructure/Service/CurrentUserWindowsProxy.cs) |

Правила программ по `process_path` применяются TUN-слоем. Прокси-программа отдает ядру соединение через HTTP/SOCKS5;
ее исходный путь не становится условием `process_path` автоматически. При совместной работе входов нельзя молча
обещать одинаковое поведение списка программ без отдельной реализации сопоставления и тестов. Правила сайтов
остаются в Xray. В `AllTraffic` пользовательские правила `Direct` отфильтровываются, `Proxy` и `Block` сохраняются.

### Обычный HTTP к адресу FakeDNS

У Xray-core v26.10.21 обработчик обычного HTTP на SOCKS/HTTP-входе заменяет контекст соединения
и теряет настройку sniffing этого входа. Поэтому запрос к имени может работать, а запрос к адресу
`198.18.0.0/15`, ранее выданному этим же ядром, завершаться без HTTP-ответа. Это обнаружил
нативный тест Windows в [Actions #446](https://github.com/rvburov/private-gateway-client/actions/runs/41316955616):
запрос через TUN прошел, дополнительный HTTP-прокси вернул `ResponseEnded`.

Для совместного режима только fake IP с `local-proxy-in` направляется отдельным выходом Shadowsocks
в уже существующий защищенный вход TUN этого же Xray на `127.0.0.1`. Используются его порт и ключ
на подключение: новый слушатель и второй VPN не создаются. На повторном входе восстанавливается
имя и применяются обычные правила сайтов, адресов и локальной сети. Другой тег входа исключает
повторную отправку по этому внутреннему пути. Остаточный fake IP без записи в таблице закрывается
до правила разрешенной LAN. При выключенном FakeDNS дополнительного пути нет (решение 524).

Встроенный выход Xray `loopback` для этого не подходит: он задает `SkipDNSResolve` и отключает
второй проход `IPIfNonMatch`, который нужен для правил IP/подсетей после восстановления имени.
Исходники ядра изучены без переноса кода, источники закреплены в [реестре](../sources/REUSE_REGISTER.md).

## Границы компонентов

Интерфейс не создает TUN, не ставит маршруты и правила брандмауэра. Это работа привилегированной службы.
Пользовательские настройки системного прокси требуют правильного сеанса: окно может вызывать платформенный
адаптер через Infrastructure, но не выполняет такие операции непосредственно из представления или модели UI.
На macOS настройками сетевых служб управляет установленная root-служба. Владение настройкой и снимком должно быть
однозначным, чтобы два компонента не возвращали разные прежние значения.

Оконное управление согласуется через capability `user-proxy-window-v1`, подтвержденный `LocalProxyPort` и признак
`UserProxyManagedByWindow` статуса службы. Старому статусу без этих полей нельзя приписывать готовность по желаемому
`ProxyPort`. Во время переподключения сохраняется ранее примененный адрес. Выбор исходного процесса для правил
TUN и учет скорости TUN не распространяются автоматически на соединения прямого HTTP/SOCKS5-входа.

Маршрут к серверу и блокировка описаны в [core.md](core.md)
и [kill-switch.md](kill-switch.md). После исправления исключение всего IP не используется: отдельный выход
sing-box `server-direct` сопоставляет адреса сервера и `ServerPort`. DNS туннеля идет в Xray перед этим правилом.

Перед первым включением из открытой сети профиль проверяется настоящим Xray до TUN и новой блокировки, в общем
таймауте подключения. При действующем блоке или опубликованном ядре этот этап не открывает защиту: действует
ограниченная защищенная попытка с уведомлением. Успех старого пинга не заменяет проверку, старое «нет» не является
вечным запретом. Повторы перебирают только ранее разрешенные IP того же профиля, сохраняя набор firewall endpoints.

Если привязанный к физической карте путь не ответил, тот же профиль и IP могут проверяться без принудительной
привязки в оставшемся общем сроке. Только настоящий ответ выбирает этот fallback для сессии; `outside: string.Empty`
передается во всех Init/Resume/Reconnect, обычный Confirm после создания TUN сохраняется. В таком случае транспорт
может снова пройти через `server-direct`: дополнительная обработка и счетчики скорости отличаются от быстрого
привязанного пути. Другие порты адреса сервера остаются в туннеле, проверка TLS не отключается.

Незавершенный запуск хранится в `_pendingTunnel` отдельно от публикации. Остановка под `_switch` отменяет и ожидает
только создание/очистку, а не publisher, который сам ждет `_switch`. Это предотвращает поздний TUN после завершенного
Disconnect и взаимную блокировку. Ожидание временного ядра предварительной проверки также заканчивается до Off.

## Платформы

| ОС | Реализация |
| --- | --- |
| Windows | [WfpKillSwitch.cs](../../desktop/src/Client.Platform.Windows/WfpKillSwitch.cs), [HyperVFirewall.cs](../../desktop/src/Client.Platform.Windows/HyperVFirewall.cs), [WindowsSystemProxy.cs](../../desktop/src/Client.Platform.Windows/WindowsSystemProxy.cs) |
| macOS | [PfKillSwitch.cs](../../desktop/src/Client.Platform.MacOS/PfKillSwitch.cs), [ScutilDns.cs](../../desktop/src/Client.Platform.MacOS/ScutilDns.cs), [NetworksetupSystemProxy.cs](../../desktop/src/Client.Platform.MacOS/NetworksetupSystemProxy.cs) |
| Linux | [NftablesKillSwitch.cs](../../desktop/src/Client.Platform.Linux/NftablesKillSwitch.cs), [GnomeSystemProxy.cs](../../desktop/src/Client.Platform.Linux/GnomeSystemProxy.cs), [KdeSystemProxy.cs](../../desktop/src/Client.Platform.Linux/KdeSystemProxy.cs) |
| Android | [GatewayVpnService.kt](../../android/app/src/main/kotlin/com/privategateway/client/vpn/GatewayVpnService.kt), [TunPlan.kt](../../android/core/src/main/kotlin/com/privategateway/client/core/vpn/TunPlan.kt) |
| iOS | [TunnelController.swift](../../ios/App/Sources/Tunnel/TunnelController.swift), [PacketTunnelProvider.swift](../../ios/PacketTunnel/Sources/PacketTunnelProvider.swift), [TunPlan.swift](../../ios/Core/Sources/PrivateGatewayCore/Vpn/TunPlan.swift) |

У мобильных приложений нет полного набора десктопных `RoutingSettings` и прокси-режимов. На Android сокеты ядра
защищаются `VpnService.protect`; на iOS ОС сама выводит соединения расширения из его туннеля. Android без адреса
и разрешения IPv6 закрывает это семейство; iOS получает оба маршрута всегда, а неразрешенный IPv6 блокирует ядро.
Системные блокировки Android и iOS отличаются от постоянных фильтров десктопа; подробности —
[android.md](android.md#туннель) и [ios.md](ios.md#управление-туннелем-из-приложения).

Матрица уровня подтверждения, виртуальные машины и пользовательские примеры —
[в руководстве по сети](../user/network.md#12-что-реализовано-и-что-нужно-проверять).
