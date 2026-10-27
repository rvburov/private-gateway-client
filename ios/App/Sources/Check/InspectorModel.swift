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
/// включенной защите — через туннель. Ничего не пишет в журнал: адреса и узлы — на экране и только.
@MainActor
final class InspectorModel: ObservableObject {
    @Published private(set) var networks: [PhoneNetwork] = []
    @Published private(set) var ip = PublicIp()
    /// Поля замера правит экран, итог пишет модель.
    @Published var probe = ProbeState()

    private var monitor: NWPathMonitor?
    private let session: URLSession

    init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 10
        configuration.timeoutIntervalForResource = 20
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        session = URLSession(configuration: configuration)
    }

    /// Экран открыт: следить за сетями. Закрыт — `stop`.
    func start() {
        guard monitor == nil else {
            return
        }

        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [weak self] path in
            let networks = Self.networks(path)
            Task { @MainActor [weak self] in
                self?.networks = networks
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

    /// Внешний IP: при включенной защите — адрес выхода сервера, без нее — провайдера. Сведения о прежнем адресе сбрасываются.
    func measureIp(protected: Bool) async {
        ip = PublicIp(measuring: true)
        let session = session
        let body = await Self.text(PublicAddress.trace, session)
        if let address = PublicAddress.traceIp(body) {
            ip = PublicIp(address: address, viaServer: protected)
        } else {
            ip = PublicIp(failed: true)
        }
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
