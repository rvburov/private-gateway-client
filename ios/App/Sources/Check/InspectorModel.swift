import Foundation
import Network
import PrivateGatewayCore

/// Внешний IP на «Проверке»: адрес, через сервер ли он измерен, идет ли замер, не ответил ли источник, сведения о провайдере.
struct PublicIp: Equatable {
    var address: String?
    var viaServer = false
    var measuring = false
    var failed = false
    var describing = false
    var info: AddressInfo?
}

/// Почему замер не начался: узел не узел, порт не порт.
enum ProbeError: Equatable, Sendable {
    case target
    case port
}

/// Замер на «Проверке»: вид, узел и порт как ввел пользователь, идет ли, итог, ошибка ввода.
struct ProbeState: Equatable, Sendable {
    var kind = ProbeKind.dns
    var target = ""
    var port = "443"
    var running = false
    var result: ProbeResult?
    var error: ProbeError?
}

/// Инспектор сети на «Проверке» — как `InspectorViewModel` Android, в пределах того, что iOS дает приложению: сети iPhone
/// (`NWPathMonitor`: вид, имя, по умолчанию ли, дорогая ли, есть ли выход в интернет), внешний IP (Cloudflare trace) и что
/// о нем знает RIPEstat — только по кнопке, замеры DNS, TCP и TLS (`ProbeRunner`). Запросы идут из процесса приложения: при
/// включенной защите — через туннель. Ничего не пишет в журнал: адреса и узлы — на экране и только. Тот же замер внешнего IP
/// и та же сеть — на главной (`WebIp`, `Uplink`).
@MainActor
final class InspectorModel: ObservableObject {
    @Published private(set) var networks: [PhoneNetwork] = []
    @Published private(set) var ip = PublicIp()
    /// Поля замера правит экран, итог пишет модель.
    @Published var probe = ProbeState()
    /// Внешний IP для главной: адрес выхода под защитой и реальный — без нее, в этой же сети.
    @Published private(set) var web = WebIp()
    /// Через какую сеть iPhone выходит в интернет; nil — iOS еще не сказала.
    @Published private(set) var uplink: Uplink?
    /// Сеть, в которой мерили реальный IP: вид и интерфейс. Сменилась — прежний реальный IP не годится (как у службы десктопа).
    @Published private(set) var networkKey: String?

    private var monitor: NWPathMonitor?
    private let session: URLSession
    /// Номер замера внешнего IP: итог прежнего, запоздавшего, не затирает новый.
    private var measureCount = 0

    init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 10
        configuration.timeoutIntervalForResource = 20
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        session = URLSession(configuration: configuration)
    }

    /// Окно открыто: следить за сетями (строка сети главной и «Проверка»). `stop` — перестать.
    func start() {
        guard monitor == nil else {
            return
        }

        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [weak self] path in
            let networks = Self.networks(path)
            let uplink = Self.uplink(of: path)
            Task { @MainActor [weak self] in
                self?.networks = networks
                self?.update(uplink.kind, interface: uplink.interface)
            }
        }
        monitor.start(queue: DispatchQueue(label: "com.privategateway.client.inspect"))
        self.monitor = monitor
    }

    func stop() {
        monitor?.cancel()
        monitor = nil
    }

    /// Сети пути iOS: первая — по умолчанию (iOS ставит ее первой); дорогая, экономия данных и выход в интернет — свойства
    /// пути, они относятся к ней.
    nonisolated static func networks(_ path: NWPath) -> [PhoneNetwork] {
        let supports = (ipv4: path.supportsIPv4, ipv6: path.supportsIPv6, dns: path.supportsDNS)
        let list = path.availableInterfaces.enumerated().map { index, interface in
            PhoneNetwork(
                kind: kind(interface), name: interface.name, isDefault: index == 0, expensive: index == 0 && path.isExpensive,
                constrained: index == 0 && path.isConstrained, satisfied: path.status == .satisfied, ipv4: supports.ipv4, ipv6: supports.ipv6,
                dns: supports.dns
            )
        }
        return PhoneNetworks.order(list)
    }

    /// Сеть выхода в интернет: под VPN путь идет через туннель, а `usesInterfaceType` называет сеть под ним. Пути нет —
    /// `offline`; вид не назван — `other`.
    nonisolated static func uplink(of path: NWPath) -> (kind: Uplink, interface: String?) {
        guard path.status == .satisfied else {
            return (.offline, nil)
        }

        let kinds: [(type: NWInterface.InterfaceType, kind: Uplink)] = [(.wifi, .wifi), (.cellular, .cellular), (.wiredEthernet, .ethernet)]
        for pair in kinds where path.usesInterfaceType(pair.type) {
            return (pair.kind, path.availableInterfaces.first { $0.type == pair.type }?.name)
        }

        for interface in path.availableInterfaces {
            if let pair = kinds.first(where: { $0.type == interface.type }) {
                return (pair.kind, interface.name)
            }
        }

        return (.other, nil)
    }

    /// Новая сеть выхода: другая сеть — реальный IP прежней сети забывается (`NetworkKey`).
    private func update(_ kind: Uplink, interface: String?) {
        uplink = kind
        let next = NetworkKey.next(networkKey, kind: kind, interface: interface)
        if next.changed {
            web.direct = nil
        }

        networkKey = next.key
    }

    private nonisolated static func kind(_ interface: NWInterface) -> PhoneNetworkKind {
        switch interface.type {
        case .wifi: return .wifi
        case .cellular: return .cellular
        case .wiredEthernet: return .ethernet
        default:
            // Туннели iOS называет utun и ipsec, вид у них — «другое».
            return interface.name.hasPrefix("utun") || interface.name.hasPrefix("ipsec") ? .vpn : .other
        }
    }

    /// Внешний IP: при включенной защите — адрес выхода сервера, без нее — провайдера (реальный). Сведения о прежнем адресе
    /// сбрасываются; итог запоздавшего замера, когда начался новый, не считается. Под защитой прежний адрес выхода до итога
    /// не показывается: сервер мог смениться.
    func measureIp(protected: Bool) async {
        measureCount += 1
        let number = measureCount
        ip = PublicIp(measuring: true)
        web.checking = true
        if protected {
            web.exit = nil
        }

        let session = session
        let body = await Self.text(PublicAddress.trace, session)
        guard number == measureCount else {
            return
        }

        let address = PublicAddress.traceIp(body)
        if let address {
            ip = PublicIp(address: address, viaServer: protected)
        } else {
            ip = PublicIp(failed: true)
        }

        web.checking = false
        if protected {
            web.exit = address
            web.checkedAt = Date()
        } else if let address {
            web.direct = address
        }
    }

    /// Защита больше не «Защищено»: адрес выхода и время проверки устарели — следующее подключение измерит заново.
    func forgetExit() {
        web.exit = nil
        web.checkedAt = nil
    }

    /// Кто провайдер: частный адрес — не спрашивать; иначе обзор сети и место из RIPEstat — запросы только с самим адресом.
    func describe() async {
        guard let address = ip.address else {
            return
        }

        guard PublicAddress.isPublic(address) else {
            ip.info = .failed(address, .private)
            return
        }

        ip.describing = true
        let requests = PublicAddress.requests(address)
        let session = session
        async let overview = Self.text(requests[0], session)
        async let geo = Self.text(requests[1], session)
        let info = PublicAddress.parse(address, overview: await overview, geo: await geo)
        ip.describing = false
        ip.info = info
    }

    /// Замер по введенному: узел — имя или IP (правила десктопа), порт — у TCP и TLS. Идет замер — второй не начинается.
    func runProbe() async {
        guard !probe.running else {
            return
        }

        let target = ProbeTarget.normalize(probe.target)
        let port = ProbeTarget.port(probe.port)
        guard let target else {
            probe.error = .target
            probe.result = nil
            return
        }

        guard probe.kind == .dns || port != nil else {
            probe.error = .port
            probe.result = nil
            return
        }

        let kind = probe.kind
        probe.running = true
        probe.error = nil
        probe.result = nil
        let result: ProbeResult
        switch kind {
        case .dns: result = await ProbeRunner.dns(target)
        case .tcp: result = await ProbeRunner.tcp(target, port ?? 0)
        case .tls: result = await ProbeRunner.tls(target, port ?? 0)
        }

        probe.running = false
        probe.result = result
    }

    /// Текст ответа 2xx не больше мегабайта; нет ответа — nil.
    private nonisolated static func text(_ address: String, _ session: URLSession) async -> String? {
        guard let url = URL(string: address), let (data, response) = try? await session.data(from: url),
              let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode), data.count <= 1_048_576
        else {
            return nil
        }

        return String(decoding: data, as: UTF8.self)
    }
}
