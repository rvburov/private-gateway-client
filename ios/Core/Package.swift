// swift-tools-version:6.0
// Общая логика iOS: разбор ссылок и подписок, описание сервера, конфиг ядра (PrivateGatewayCore), тема и тексты из
// shared/specifications (PrivateGatewayTheme) — без UIKit, SwiftUI и NetworkExtension, поэтому пакет собирается и тестируется
// и на Linux (CI), и на macOS. Приложение и расширение Packet Tunnel подключают его как библиотеки.
import PackageDescription

let package = Package(
    name: "PrivateGatewayCore",
    platforms: [.iOS(.v16), .macOS(.v13)],
    products: [
        .library(name: "PrivateGatewayCore", targets: ["PrivateGatewayCore"]),
        .library(name: "PrivateGatewayTheme", targets: ["PrivateGatewayTheme"]),
    ],
    dependencies: [
        // YAML конфигов Clash/Mihomo — libyaml внутри Yams (MIT); версия закреплена, как у пакетов десктопа и Android.
        .package(url: "https://github.com/jpsim/Yams.git", exact: "5.1.3"),
    ],
    targets: [
        .target(name: "PrivateGatewayCore", dependencies: ["Yams"]),
        // Тема, тексты и иконки из shared/specifications: плагин SharedResources запускает SharedGen (логика — SharedGenKit)
        // при каждой сборке PrivateGatewayTheme, как build-logic у Android.
        .target(name: "SharedGenKit"),
        .executableTarget(name: "SharedGen", dependencies: ["SharedGenKit"]),
        .plugin(name: "SharedResources", capability: .buildTool(), dependencies: ["SharedGen"]),
        .target(name: "PrivateGatewayTheme", plugins: [.plugin(name: "SharedResources")]),
        .testTarget(name: "PrivateGatewayCoreTests", dependencies: ["PrivateGatewayCore"]),
        .testTarget(name: "PrivateGatewayThemeTests", dependencies: ["PrivateGatewayTheme", "SharedGenKit"]),
    ]
)
