import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Подделка команд туннелю: только счет и ошибка подключения, которую задал тест.
final class FakeControl: TunnelControl, @unchecked Sendable {
    private let lock = NSLock()
    private var counts = (connects: 0, disconnects: 0)
    var failure: (any Error)?

    var connects: Int { locked { counts.connects } }
    var disconnects: Int { locked { counts.disconnects } }

    func connect() async throws {
        let error = locked { () -> (any Error)? in
            counts.connects += 1
            return failure
        }
        if let error {
            throw error
        }
    }

    func disconnect() async {
        locked { counts.disconnects += 1 }
    }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }
}

/// Главная и действия над серверами — те же случаи, что `MainViewModelTest` Android: выбор только нажатием, переход защиты на
/// новый сервер, удаление сервера в работе, свое название; что показать по состоянию системы и отчету расширения.
final class HomeStateTests: XCTestCase {
    private let vault = MemoryVault()
    private lazy var servers = ServerStore(file: MemoryFile(), vault: vault)
    private lazy var settings = SettingsStore(file: MemoryFile())
    private let control = FakeControl()
    private lazy var actions = ServerActions(servers: servers, settings: settings, control: control)

    /// Три разных эталонных сервера в хранилище; номера — по порядку.
    private func three() async throws -> [String] {
        try await servers.add([
            TunnelLaunchTests.fixture("vless/reality-vision"), TunnelLaunchTests.fixture("trojan/ws"), TunnelLaunchTests.fixture("vmess/ws-tls"),
        ])
        let items = try await servers.servers()
        XCTAssertEqual(items.count, 3)
        return items.map(\.id)
    }

    private func item(_ id: String, linkName: String = "server") -> ServerItem {
        ServerItem(id: id, linkName: linkName, countryCode: "NL", protocol: .vless, security: .reality, transport: .tcp, port: 443)
    }

    // MARK: Главная

    func testInUseIsTheTunnelServerOrTheSelectedOne() {
        let servers = [item("a"), item("b")]
        var state = HomeState(status: TunnelStatus(state: .connected, serverId: "b"), servers: servers, selectedId: "a")
        XCTAssertEqual(state.inUse?.id, "b", "защита идет через b")
        XCTAssertTrue(state.active("b"))
        XCTAssertFalse(state.active("a"))

        state.status = TunnelStatus()
        XCTAssertEqual(state.inUse?.id, "a", "выключено — выбранный")
        XCTAssertFalse(state.active("a"))

        state.status = TunnelStatus(state: .error, error: TunnelErrors.coreFailed)
        XCTAssertFalse(state.active("a"))
    }

    func testPowerAddsChoosesOrToggles() {
        XCTAssertEqual(HomeState().power, .add, "серверов нет")
        XCTAssertEqual(HomeState(servers: [item("a")]).power, .choose, "серверы есть, ни один не выбран")
        XCTAssertEqual(HomeState(servers: [item("a")], selectedId: "a").power, .toggle)
        XCTAssertEqual(HomeState(status: TunnelStatus(state: .connecting)).power, .toggle, "идущую защиту можно выключить всегда")
        XCTAssertEqual(HomeState(servers: [item("a")], selectedId: "gone").power, .choose, "выбранный удален")
    }

    // MARK: Состояние системы и отчет расширения

    func testSystemStateWinsAndTheReportAddsDetails() {
        let since = Date(timeIntervalSince1970: 1_793_029_320)
        let connected = TunnelStatus(state: .connected, serverId: "a", since: since)
        XCTAssertEqual(TunnelView.status(system: .connected, report: connected), connected)
        XCTAssertEqual(TunnelView.status(system: .connecting, report: TunnelStatus(state: .connecting, serverId: "a")), TunnelStatus(state: .connecting, serverId: "a"))
        XCTAssertEqual(TunnelView.status(system: .reasserting, report: connected), TunnelStatus(state: .reconnecting, serverId: "a", attempt: 1))
        XCTAssertEqual(TunnelView.status(system: .disconnecting, report: connected), TunnelStatus(state: .stopping, serverId: "a"))

        // Время в сети — только из отчета о подключении.
        XCTAssertNil(TunnelView.status(system: .connected, report: TunnelStatus(state: .connecting, serverId: "a")).since)
    }

    func testDisconnectedShowsTheErrorOrOff() {
        let failed = TunnelStatus(state: .error, error: TunnelErrors.noServer)
        XCTAssertEqual(TunnelView.status(system: .disconnected, report: failed), failed)
        // Расширение упало, не дописав файл: система говорит «выключено» — так и показываем.
        XCTAssertEqual(TunnelView.status(system: .disconnected, report: TunnelStatus(state: .connected, serverId: "a")), TunnelStatus())
        XCTAssertEqual(TunnelView.status(system: .invalid, report: nil), TunnelStatus())
        // Отказ самого приложения важнее старого отчета.
        XCTAssertEqual(
            TunnelView.status(system: .disconnected, report: failed, appError: TunnelErrors.otherVpn), TunnelStatus(state: .error, error: TunnelErrors.otherVpn)
        )
        XCTAssertEqual(TunnelView.status(system: .connected, report: nil, appError: TunnelErrors.otherVpn).state, .connected, "туннель поднят — отказа нет")
    }

    func testVpnProfileFollowsKillSwitchAndLocalNetwork() {
        XCTAssertEqual(VpnProfile.of(AppSettings()), VpnProfile(includeAllNetworks: true, excludeLocalNetworks: false, enforceRoutes: false))
        XCTAssertEqual(
            VpnProfile.of(AppSettings(tunnel: TunnelSettings(allowLan: true))), VpnProfile(includeAllNetworks: true, excludeLocalNetworks: true, enforceRoutes: false)
        )
        // Без блока при обрыве закрытая локальная сеть держится маршрутами туннеля.
        XCTAssertEqual(VpnProfile.of(AppSettings(killSwitch: false)), VpnProfile(includeAllNetworks: false, excludeLocalNetworks: false, enforceRoutes: true))
        XCTAssertEqual(
            VpnProfile.of(AppSettings(tunnel: TunnelSettings(allowLan: true), killSwitch: false)),
            VpnProfile(includeAllNetworks: false, excludeLocalNetworks: true, enforceRoutes: false)
        )
    }

    // MARK: Действия

    func testWithoutProtectionATapOnlySelects() async throws {
        let ids = try await three()
        try await actions.select(ids[1], state: .off)
        let selected = try await settings.load().selectedServerId
        XCTAssertEqual(selected, ids[1])
        XCTAssertEqual(control.connects, 0)
        let recent = try await servers.servers().first { $0.id == ids[1] }?.lastUsedAt
        XCTAssertNotNil(recent, "выбранный — в «Недавних»")
    }

    func testAnotherServerUnderProtectionReconnectsAtOnce() async throws {
        let ids = try await three()
        try await actions.select(ids[0], state: .off)
        try await actions.select(ids[1], state: .connected)
        XCTAssertEqual(control.connects, 1)
        try await actions.select(ids[2], state: .stopping)
        XCTAssertEqual(control.connects, 1, "защита выключается — только выбор")
    }

    func testTheSameServerChangesNothing() async throws {
        let ids = try await three()
        try await actions.select(ids[0], state: .off)
        let first = try await servers.servers().first { $0.id == ids[0] }?.lastUsedAt
        try await actions.select(ids[0], state: .connected)
        XCTAssertEqual(control.connects, 0)
        let again = try await servers.servers().first { $0.id == ids[0] }?.lastUsedAt
        XCTAssertEqual(again, first, "«Недавние» не тронуты")
    }

    func testConnectFromTheCardStartsProtectionAfterTheChoiceIsStored() async throws {
        let ids = try await three()
        try await actions.connectTo(ids[2], state: .off)
        XCTAssertEqual(control.connects, 1)
        let selected = try await settings.load().selectedServerId
        XCTAssertEqual(selected, ids[2])

        try await actions.connectTo(ids[2], state: .connected)
        XCTAssertEqual(control.connects, 1, "уже идет через него")
        try await actions.connectTo(ids[0], state: .connected)
        XCTAssertEqual(control.connects, 2)
        try await actions.connectTo(ids[0], state: .error)
        XCTAssertEqual(control.connects, 3, "после ошибки — снова включить")
    }

    func testDeletingTheServerInUseDisconnectsFirstAndClearsTheChoice() async throws {
        let ids = try await three()
        try await actions.select(ids[0], state: .off)
        try await actions.delete(ids[0], status: TunnelStatus(state: .connected, serverId: ids[0]))
        XCTAssertEqual(control.disconnects, 1)
        let selected = try await settings.load().selectedServerId
        XCTAssertNil(selected, "другой сервер сам не выбирается")
        let left = try await servers.servers().map(\.id)
        XCTAssertEqual(left, [ids[1], ids[2]])

        try await actions.delete(ids[1], status: TunnelStatus(state: .connected, serverId: ids[2]))
        XCTAssertEqual(control.disconnects, 1, "удален не тот, через который идет защита")
    }

    func testRenameKeepsAValidOwnNameAndTheLinkNameClearsIt() async throws {
        let ids = try await three()
        try await actions.rename(ids[0], "  Дом  ")
        var server = try await servers.servers().first { $0.id == ids[0] }
        XCTAssertEqual(server?.customName, "Дом")

        try await actions.rename(ids[0], String(repeating: "я", count: 65))
        server = try await servers.servers().first { $0.id == ids[0] }
        XCTAssertEqual(server?.customName, "Дом", "длинное не сохраняется")

        let linkName = try XCTUnwrap(server?.linkName)
        try await actions.rename(ids[0], linkName)
        server = try await servers.servers().first { $0.id == ids[0] }
        XCTAssertNil(server?.customName, "название из ссылки — своего нет")
    }

    func testFavoriteIsStored() async throws {
        let ids = try await three()
        try await actions.setFavorite(ids[1], true)
        let favorite = try await servers.servers().first { $0.id == ids[1] }?.favorite
        XCTAssertEqual(favorite, true)
    }
}
