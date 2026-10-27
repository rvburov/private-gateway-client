import Foundation
import Network
import PrivateGatewayCore
import Security

/// Замеры инспектора из процесса приложения — как `ProbeRunner` Android: поиск имени, соединение TCP, рукопожатие TLS. Под
/// защитой приложение идет через туннель — замер видит сеть так же, как программы iPhone. Ничего не пишет в журнал: узел —
/// то, что ввел пользователь.
enum ProbeRunner {
    /// Сколько ждать соединения и рукопожатия — как у десктопа и Android.
    static let timeout = 5

    private static let queue = DispatchQueue(label: "com.privategateway.client.probe")

    /// Поиск имени через DNS системы — те же адреса получают программы.
    static func dns(_ target: String) async -> ProbeResult {
        let started = DispatchTime.now()
        let addresses = await resolve(target)
        let ms = elapsed(started)
        guard !addresses.isEmpty else {
            return ProbeResult(kind: .dns, target: target, port: nil, steps: [ProbeStep(index: 1, address: nil, timeMs: nil, outcome: .notFound)])
        }

        let steps = addresses.enumerated().map { ProbeStep(index: $0.offset + 1, address: $0.element, timeMs: ms, outcome: .ok) }
        return ProbeResult(kind: .dns, target: target, port: nil, steps: steps)
    }

    /// Соединение TCP без данных: порт открыт, если узел ответил на рукопожатие.
    static func tcp(_ target: String, _ port: Int) async -> ProbeResult {
        await connect(.tcp, target, port)
    }

    /// Рукопожатие TLS без отправки данных: версия, шифр, ALPN и сертификат. Сертификат проверяется отдельно от рукопожатия
    /// (доверенные корни iPhone, срок, имя) — так видно, что именно с ним не так. Отзыв сертификата не проверяется.
    static func tls(_ target: String, _ port: Int) async -> ProbeResult {
        await connect(.tls, target, port)
    }

    /// Адреса имени через DNS системы (getaddrinfo), без повторов; IP — он сам. Имени нет — пусто.
    static func resolve(_ target: String) async -> [String] {
        await withCheckedContinuation { (continuation: CheckedContinuation<[String], Never>) in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(returning: ProbeRunner.addresses(target))
            }
        }
    }

    private static func addresses(_ target: String) -> [String] {
        var hints = addrinfo()
        hints.ai_family = AF_UNSPEC
        hints.ai_socktype = SOCK_STREAM
        var list: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(target, nil, &hints, &list) == 0, let first = list else {
            return []
        }

        defer { freeaddrinfo(first) }
        var result: [String] = []
        var next: UnsafeMutablePointer<addrinfo>? = first
        while let item = next {
            next = item.pointee.ai_next
            guard let address = item.pointee.ai_addr else {
                continue
            }

            var bytes: [UInt8]?
            switch Int32(address.pointee.sa_family) {
            case AF_INET:
                bytes = address.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { pointer in withUnsafeBytes(of: pointer.pointee.sin_addr) { Array($0) } }
            case AF_INET6:
                bytes = address.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { pointer in withUnsafeBytes(of: pointer.pointee.sin6_addr) { Array($0) } }
            default:
                bytes = nil
            }

            if let bytes {
                let text = AddressText.of(bytes)
                if !result.contains(text) {
                    result.append(text)
                }
            }
        }

        return result
    }

    /// Соединение с первым адресом имени (TCP или TLS); итог — один шаг: кто ответил, за сколько, чем кончилось.
    private static func connect(_ kind: ProbeKind, _ target: String, _ port: Int) async -> ProbeResult {
        guard let address = await resolve(target).first, let number = UInt16(exactly: port), let endpoint = NWEndpoint.Port(rawValue: number) else {
            return ProbeResult(kind: kind, target: target, port: port, steps: [ProbeStep(index: 1, address: nil, timeMs: nil, outcome: .notFound)])
        }

        let chain = ChainCapture()
        let tcp = NWProtocolTCP.Options()
        tcp.connectionTimeout = timeout
        let parameters = NWParameters(tls: kind == .tls ? tlsOptions(target, chain) : nil, tcp: tcp)
        let connection = NWConnection(host: NWEndpoint.Host(address), port: endpoint, using: parameters)
        let started = DispatchTime.now()
        let failure = await ready(connection)
        let ms = elapsed(started)
        let step: ProbeStep
        if let failure {
            step = ProbeStep(index: 1, address: address, timeMs: nil, outcome: failure)
        } else if kind == .tls {
            step = handshake(connection, chain, target, address, ms)
        } else {
            step = ProbeStep(index: 1, address: address, timeMs: ms, outcome: .ok)
        }

        connection.cancel()
        return ProbeResult(kind: kind, target: target, port: port, steps: [step])
    }

    /// TLS с именем узла (SNI; у IP — без него) и ALPN «h2, http/1.1». Сертификат принимается на время рукопожатия и
    /// запоминается вместе с итогом проверки цепочки — по пунктам он разбирается потом.
    private static func tlsOptions(_ target: String, _ chain: ChainCapture) -> NWProtocolTLS.Options {
        let tls = NWProtocolTLS.Options()
        let options = tls.securityProtocolOptions
        if ProbeTarget.ip(target) == nil {
            sec_protocol_options_set_tls_server_name(options, target)
        }

        sec_protocol_options_add_tls_application_protocol(options, "h2")
        sec_protocol_options_add_tls_application_protocol(options, "http/1.1")
        sec_protocol_options_set_verify_block(options, { _, trust, complete in
            chain.keep(sec_trust_copy_ref(trust).takeRetainedValue())
            complete(true)
        }, queue)
        return tls
    }

    /// Сведения рукопожатия: версия, шифр, ALPN, сертификат и что с ним не так. Сертификата нет — замер не удался.
    private static func handshake(_ connection: NWConnection, _ chain: ChainCapture, _ target: String, _ address: String, _ ms: Int) -> ProbeStep {
        let captured = chain.result
        guard let metadata = (connection.metadata(definition: NWProtocolTLS.definition) as? NWProtocolTLS.Metadata)?.securityProtocolMetadata,
              let der = captured.der, let leaf = Certificate.parse(der)
        else {
            return ProbeStep(index: 1, address: address, timeMs: nil, outcome: .failed)
        }

        let facts = TlsFacts(
            protocol: TlsCheck.version(sec_protocol_metadata_get_negotiated_tls_protocol_version(metadata).rawValue),
            cipher: TlsCheck.cipher(sec_protocol_metadata_get_negotiated_tls_ciphersuite(metadata).rawValue),
            alpn: sec_protocol_metadata_get_negotiated_protocol(metadata).map { String(cString: $0) }.flatMap { $0.isEmpty ? nil : $0 },
            subject: leaf.subject, issuer: leaf.issuer, notBefore: leaf.notBefore, notAfter: leaf.notAfter, names: leaf.names,
            problems: TlsCheck.problems(leaf, target: target, now: Date(), chain: captured.check)
        )
        return ProbeStep(index: 1, address: address, timeMs: ms, outcome: .ok, tls: facts)
    }

    /// Ждать готовности соединения не дольше `timeout`: готово — nil, иначе — чем кончилось. «Ожидание» с ошибкой (отказ,
    /// нет сети) — тоже итог: дальше iOS только повторяла бы попытки.
    private static func ready(_ connection: NWConnection) async -> ProbeOutcome? {
        await withCheckedContinuation { (continuation: CheckedContinuation<ProbeOutcome?, Never>) in
            let once = Once(continuation)
            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    once.resume(nil)
                case let .waiting(error), let .failed(error):
                    once.resume(ProbeRunner.outcome(error))
                case .cancelled:
                    once.resume(.failed)
                default:
                    break
                }
            }
            connection.start(queue: queue)
            queue.asyncAfter(deadline: .now() + .seconds(timeout)) {
                once.resume(.timedOut)
            }
        }
    }

    private static func outcome(_ error: NWError) -> ProbeOutcome {
        switch error {
        case let .posix(code):
            switch code {
            case .ECONNREFUSED: return .refused
            case .ETIMEDOUT: return .timedOut
            case .ENETUNREACH, .EHOSTUNREACH, .ENETDOWN, .EHOSTDOWN: return .unreachable
            default: return .failed
            }
        case .dns:
            return .notFound
        default:
            return .failed
        }
    }

    private static func elapsed(_ started: DispatchTime) -> Int {
        Int((DispatchTime.now().uptimeNanoseconds - started.uptimeNanoseconds) / 1_000_000)
    }
}

/// Сертификат узла из рукопожатия и проверка его цепочки доверенными корнями iPhone (без имени узла: имя и срок
/// проверяются по пунктам). Пишет очередь замера, читает замер после рукопожатия.
private final class ChainCapture: @unchecked Sendable {
    private let lock = NSLock()
    private var der: [UInt8]?
    private var check = ChainCheck.failed(dated: false)

    var result: (der: [UInt8]?, check: ChainCheck) {
        lock.lock()
        defer { lock.unlock() }
        return (der, check)
    }

    func keep(_ trust: SecTrust) {
        let certificates = (SecTrustCopyCertificateChain(trust) as? [SecCertificate]) ?? []
        let der = certificates.first.map { [UInt8](SecCertificateCopyData($0) as Data) }
        var check = ChainCheck.failed(dated: false)
        var chain: SecTrust?
        if !certificates.isEmpty, SecTrustCreateWithCertificates(certificates as CFArray, SecPolicyCreateBasicX509(), &chain) == errSecSuccess, let chain {
            var error: CFError?
            if SecTrustEvaluateWithError(chain, &error) {
                check = .trusted
            } else {
                let code = error.map { CFErrorGetCode($0) } ?? 0
                check = .failed(dated: code == Int(errSecCertificateExpired) || code == Int(errSecCertificateNotValidYet))
            }
        }

        lock.lock()
        self.der = der
        self.check = check
        lock.unlock()
    }
}

/// Продолжение, которое возобновляется один раз: готовность, отказ и срок приходят с разных сторон.
private final class Once<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<T, Never>?

    init(_ continuation: CheckedContinuation<T, Never>) {
        self.continuation = continuation
    }

    func resume(_ value: T) {
        lock.lock()
        let pending = continuation
        continuation = nil
        lock.unlock()
        pending?.resume(returning: value)
    }
}
