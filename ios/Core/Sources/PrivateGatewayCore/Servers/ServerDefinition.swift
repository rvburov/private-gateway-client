/// Протокол сервера — те же, что у десктопа и Android (`ProxyProtocol`).
public enum ProxyProtocol: String, CaseIterable, Sendable {
    case vless
    case vmess
    case trojan
    case shadowsocks
    case hysteria2
    case tuic
    case wireGuard
}

/// Транспорт поверх TCP или UDP.
public enum TransportKind: String, Sendable {
    case tcp
    case webSocket
    case grpc
    case httpUpgrade
    case xhttp
    case quic

    /// WireGuard — свой протокол поверх UDP.
    case udp
}

/// Защита транспорта.
public enum TransportSecurity: String, Sendable {
    case none
    case tls
    case reality
}

/// Транспорт: вид, путь, заголовок Host, имя сервиса gRPC.
public struct TransportOptions: Equatable, Hashable, Sendable {
    public var kind: TransportKind
    public var path: String?
    public var host: String?
    public var serviceName: String?

    public init(_ kind: TransportKind, path: String? = nil, host: String? = nil, serviceName: String? = nil) {
        self.kind = kind
        self.path = path
        self.host = host
        self.serviceName = serviceName
    }

    public static let tcp = TransportOptions(.tcp)
    public static let quic = TransportOptions(.quic)
    public static let udp = TransportOptions(.udp)
}

/// Защита: TLS или REALITY с именем, отпечатком, ALPN и ключами REALITY. Сертификат TLS можно закрепить (решение 480):
/// `pinnedPeerCertSha256` — SHA-256 сертификата сервера или его центра (64 знака hex, строчные; из ссылки — «pcs»),
/// `verifyPeerCertByName` — имена, на которые проверяется сертификат вместо SNI («vcn»). `allowInsecure` ядро не берет (решение 477).
public struct SecurityOptions: Equatable, Hashable, Sendable {
    public var kind: TransportSecurity
    public var sni: String?
    public var fingerprint: String?
    public var alpn: [String]?
    public var allowInsecure: Bool
    public var realityPublicKey: String?
    public var realityShortId: String?
    public var realitySpiderX: String?
    public var pinnedPeerCertSha256: [String]?
    public var verifyPeerCertByName: [String]?

    public init(
        _ kind: TransportSecurity,
        sni: String? = nil,
        fingerprint: String? = nil,
        alpn: [String]? = nil,
        allowInsecure: Bool = false,
        realityPublicKey: String? = nil,
        realityShortId: String? = nil,
        realitySpiderX: String? = nil,
        pinnedPeerCertSha256: [String]? = nil,
        verifyPeerCertByName: [String]? = nil
    ) {
        self.kind = kind
        self.sni = sni
        self.fingerprint = fingerprint
        self.alpn = alpn
        self.allowInsecure = allowInsecure
        self.realityPublicKey = realityPublicKey
        self.realityShortId = realityShortId
        self.realitySpiderX = realitySpiderX
        self.pinnedPeerCertSha256 = pinnedPeerCertSha256
        self.verifyPeerCertByName = verifyPeerCertByName
    }

    public static let none = SecurityOptions(.none)
}

/// Параметры протокола: flow VLESS, шифр, obfs Hysteria2, контроль перегрузки TUIC, поля WireGuard.
public struct ProtocolOptions: Equatable, Hashable, Sendable {
    public var flow: String?
    public var cipher: String?
    public var obfs: String?
    public var congestionControl: String?
    public var peerPublicKey: String?
    public var addresses: [String]?
    public var mtu: Int?

    public init(
        flow: String? = nil,
        cipher: String? = nil,
        obfs: String? = nil,
        congestionControl: String? = nil,
        peerPublicKey: String? = nil,
        addresses: [String]? = nil,
        mtu: Int? = nil
    ) {
        self.flow = flow
        self.cipher = cipher
        self.obfs = obfs
        self.congestionControl = congestionControl
        self.peerPublicKey = peerPublicKey
        self.addresses = addresses
        self.mtu = mtu
    }

    public static let empty = ProtocolOptions()
}

/// Секреты сервера; в тексте (журнал, отладка) не показываются.
public struct ServerCredentials: Equatable, Hashable, Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    public var uuid: String?
    public var password: String?
    public var obfsPassword: String?
    public var privateKey: String?
    public var presharedKey: String?

    public init(uuid: String? = nil, password: String? = nil, obfsPassword: String? = nil, privateKey: String? = nil, presharedKey: String? = nil) {
        self.uuid = uuid
        self.password = password
        self.obfsPassword = obfsPassword
        self.privateKey = privateKey
        self.presharedKey = presharedKey
    }

    public var description: String { "ServerCredentials { *** }" }

    public var debugDescription: String { description }
}

/// Описание сервера — как его передают ядру: то же, что `ServerDefinition` десктопа и Android.
public struct ServerDefinition: Equatable, Hashable, Sendable {
    public var name: String
    public var countryCode: String
    public var `protocol`: ProxyProtocol
    public var host: String
    public var port: Int
    public var transport: TransportOptions
    public var security: SecurityOptions
    public var options: ProtocolOptions
    public var credentials: ServerCredentials

    public init(
        name: String,
        countryCode: String,
        protocol: ProxyProtocol,
        host: String,
        port: Int,
        transport: TransportOptions,
        security: SecurityOptions,
        options: ProtocolOptions,
        credentials: ServerCredentials
    ) {
        self.name = name
        self.countryCode = countryCode
        self.protocol = `protocol`
        self.host = host
        self.port = port
        self.transport = transport
        self.security = security
        self.options = options
        self.credentials = credentials
    }
}
