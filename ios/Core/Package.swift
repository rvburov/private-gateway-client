// swift-tools-version:6.0
// Общая логика iOS: разбор ссылок и подписок, описание сервера, конфиг ядра — без UIKit и NetworkExtension, поэтому пакет
// собирается и тестируется и на Linux (CI), и на macOS. Приложение и расширение Packet Tunnel подключают его как библиотеку.
import PackageDescription

let package = Package(
    name: "PrivateGatewayCore",
    platforms: [.iOS(.v16), .macOS(.v13)],
    products: [
        .library(name: "PrivateGatewayCore", targets: ["PrivateGatewayCore"]),
    ],
    dependencies: [
        // YAML конфигов Clash/Mihomo — libyaml внутри Yams (MIT); версия закреплена, как у пакетов десктопа и Android.
        .package(url: "https://github.com/jpsim/Yams.git", exact: "5.1.3"),
    ],
    targets: [
        .target(name: "PrivateGatewayCore", dependencies: ["Yams"]),
        .testTarget(name: "PrivateGatewayCoreTests", dependencies: ["PrivateGatewayCore"]),
    ]
)
