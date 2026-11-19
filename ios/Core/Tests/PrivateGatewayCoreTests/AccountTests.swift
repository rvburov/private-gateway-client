import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Вход в аккаунт сайта на iOS — те же ответы и правила, что `AccountApiTest`, `AccountGateTest` и `AccountViewModel` Android.
final class AccountTests: XCTestCase {
    private let user = #"{"email":"user@example.com","display_name":"Участник"}"#
    private let device = #"{"id":42,"name":"iPhone · iOS 17.5","platform":"ios","app_version":"0.4.0","ip_masked":"192.0.2.x"}"#
    private var loginOk: Data {
        Data(#"{"token":"tok-secret-1","expires_at":"2026-12-12T10:00:00.123456Z","user":\#(user),"device":\#(device)}"#.utf8)
    }

    private var statusOk: Data {
        Data(#"{"status":"ok","expires_at":"2026-12-20T10:00:00+00:00","user":\#(user),"device":\#(device)}"#.utf8)
    }

    private static let phone = DeviceInfo(uid: "11111111-2222-3333-4444-555555555555", name: "iPhone · iOS 17.5", appVersion: "0.4.0")

    // MARK: - Ответы сайта

    func testLoginReadsTheSession() throws {
        guard case let .ok(session) = AccountAnswers.login(status: 200, body: loginOk, errorCode: nil, retryAfter: nil) else {
            return XCTFail("ожидался вход")
        }

        XCTAssertEqual(session.token, "tok-secret-1")
        XCTAssertEqual(session.expiresAt.timeIntervalSince1970, try XCTUnwrap(AccountAnswers.date("2026-12-12T10:00:00Z")).timeIntervalSince1970 + 0.123456, accuracy: 0.0001)
        XCTAssertEqual(session.user, AccountUser(email: "user@example.com", displayName: "Участник"))
        XCTAssertEqual(session.device, AccountDevice(id: 42, name: "iPhone · iOS 17.5"))
        XCTAssertFalse(String(describing: session).contains("tok-secret"), "токена нет в описании")
    }

    func testLoginStatusesMapToReasons() {
        let cases: [(Int, String?, String?, LoginResult)] = [
            (200, nil, nil, .failed(.server)),
            (401, "code_required", nil, .codeRequired),
            (401, "invalid_credentials", nil, .failed(.invalid)),
            (401, nil, nil, .failed(.invalid)),
            (400, nil, nil, .failed(.code)),
            (403, nil, nil, .failed(.setupRequired)),
            (409, "device_change_limit", nil, .failed(.deviceChangeLimit)),
            (409, "device_limit", nil, .failed(.deviceLimit)),
            (409, "future", nil, .failed(.deviceLimit)),
            (429, nil, "61", .failed(.rateLimited, retryMinutes: 2)),
            (429, nil, nil, .failed(.rateLimited, retryMinutes: 1)),
            (422, nil, nil, .failed(.validation)),
            (500, nil, nil, .failed(.server)),
            (302, nil, nil, .failed(.server)),
        ]
        for (status, code, retry, expected) in cases {
            XCTAssertEqual(AccountAnswers.login(status: status, body: status == 200 ? Data("{}".utf8) : nil, errorCode: code, retryAfter: retry), expected, "\(status) \(code ?? "")")
        }
    }

    func testOnlyUnauthorizedRevokesAccess() throws {
        guard case let .ok(expires, user, device) = AccountAnswers.status(status: 200, body: statusOk) else {
            return XCTFail("ожидался доступ")
        }

        XCTAssertEqual(expires, try XCTUnwrap(AccountAnswers.date("2026-12-20T10:00:00Z")))
        XCTAssertEqual(user.email, "user@example.com")
        XCTAssertEqual(device.id, 42)
        XCTAssertEqual(AccountAnswers.status(status: 401, body: nil), .revoked)
        for status in [403, 404, 500, 503] {
            XCTAssertEqual(AccountAnswers.status(status: status, body: nil), .unreachable, "\(status)")
        }

        for body in [#"{"status":"revoked"}"#, "<html>", #"{"status":"ok"}"#, ""] {
            XCTAssertEqual(AccountAnswers.status(status: 200, body: Data(body.utf8)), .unreachable, body)
        }
    }

    func testErrorCodeAndRetryAfter() {
        XCTAssertEqual(AccountAnswers.errorCode(Data(#"{"error":{"code":"code_required","message":"x"}}"#.utf8)), "code_required")
        XCTAssertNil(AccountAnswers.errorCode(Data(#"{"detail":"x"}"#.utf8)))
        XCTAssertNil(AccountAnswers.errorCode(Data("not json".utf8)))
        XCTAssertNil(AccountAnswers.errorCode(nil))
        XCTAssertNil(AccountAnswers.errorCode(Data(repeating: 32, count: AccountAnswers.maxBytes + 1)), "больше предела — не разбирается")
        XCTAssertEqual(AccountAnswers.retryMinutes("1"), 1)
        XCTAssertEqual(AccountAnswers.retryMinutes(" 120 "), 2)
        XCTAssertEqual(AccountAnswers.retryMinutes("121"), 3)
        XCTAssertEqual(AccountAnswers.retryMinutes("0"), 1)
        XCTAssertEqual(AccountAnswers.retryMinutes("Wed, 21 Oct 2026 07:28:00 GMT"), 1)
    }

    func testDatesWithAndWithoutOffset() throws {
        let utc = try XCTUnwrap(AccountAnswers.date("2026-12-12T10:00:00Z"))
        XCTAssertEqual(AccountAnswers.date("2026-12-12T10:00:00+00:00"), utc)
        XCTAssertEqual(AccountAnswers.date("2026-12-12T10:00:00"), utc, "без смещения — UTC")
        XCTAssertEqual(AccountAnswers.date("2026-12-12T13:00:00+03:00"), utc)
        XCTAssertEqual(try XCTUnwrap(AccountAnswers.date("2026-12-12T10:00:00.5")).timeIntervalSince(utc), 0.5, accuracy: 0.0001)
        XCTAssertEqual(try XCTUnwrap(AccountAnswers.date("2026-12-12T10:00:00.250000+00:00")).timeIntervalSince(utc), 0.25, accuracy: 0.0001)
        for text in ["", "завтра", "2026-12-12", "2026-12-12T10:00:00.Z", "2026-13-12T10:00:00Z"] {
            XCTAssertNil(AccountAnswers.date(text), text)
        }
    }

    func testLoginBodyHasTheDeviceAndAtMostOneCode() throws {
        let first = try body(AccountAnswers.loginBody(email: " user@example.com ", password: #"pw "q""#, code: nil, recovery: nil, device: Self.phone))
        XCTAssertEqual(first["email"] as? String, "user@example.com")
        XCTAssertEqual(first["password"] as? String, #"pw "q""#)
        XCTAssertNil(first["code"])
        XCTAssertNil(first["recovery_code"])
        let phone = try XCTUnwrap(first["device"] as? [String: Any])
        XCTAssertEqual(phone["platform"] as? String, "ios")
        XCTAssertEqual(phone["uid"] as? String, Self.phone.uid)
        XCTAssertEqual(phone["name"] as? String, "iPhone · iOS 17.5")
        XCTAssertEqual(phone["app_version"] as? String, "0.4.0")
        let code = try body(AccountAnswers.loginBody(email: "a@example.com", password: "p", code: " 123456 ", recovery: nil, device: Self.phone))
        XCTAssertEqual(code["code"] as? String, "123456")
        let recovery = try body(AccountAnswers.loginBody(email: "a@example.com", password: "p", code: nil, recovery: "abcd-efgh", device: Self.phone))
        XCTAssertEqual(recovery["recovery_code"] as? String, "abcd-efgh")
        XCTAssertNil(recovery["code"])
    }

    func testDeviceNameAndUid() {
        XCTAssertTrue(DeviceInfo.validUid("11111111-2222-3333-4444-555555555555"))
        XCTAssertTrue(DeviceInfo.validUid("abc_DEF-0123456789"))
        XCTAssertFalse(DeviceInfo.validUid("short"))
        XCTAssertFalse(DeviceInfo.validUid(String(repeating: "a", count: 65)))
        XCTAssertFalse(DeviceInfo.validUid("ааааааааааааааааа"), "только латиница")
        XCTAssertFalse(DeviceInfo.validUid("aaaaaaaaaaaaaaaa aaaa"))
        XCTAssertEqual(DeviceInfo.phoneName(model: "iPhone", system: "iOS 17.5"), "iPhone · iOS 17.5")
        XCTAssertEqual(DeviceInfo.phoneName(model: "  iPad\n ", system: nil), "iPad")
        XCTAssertEqual(DeviceInfo.phoneName(model: nil, system: " "), "iPhone")
        XCTAssertEqual(DeviceInfo(uid: "u", name: String(repeating: "x", count: 150), appVersion: "1").name.count, DeviceInfo.nameMax)
    }

    // MARK: - Правило доступа

    func testVerdictTrustsOnlyTheSite() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        let later = now.addingTimeInterval(3600)
        let earlier = now.addingTimeInterval(-1)
        let ok = StatusResult.ok(expiresAt: later.addingTimeInterval(86400), user: AccountUser(email: "u@example.com", displayName: ""), device: AccountDevice(id: 1, name: ""))
        XCTAssertEqual(AccountGate.verdict(later, ok, now: now), .keep(expiresAt: later.addingTimeInterval(86400), confirmed: true))
        XCTAssertEqual(AccountGate.verdict(earlier, ok, now: now), .keep(expiresAt: later.addingTimeInterval(86400), confirmed: true), "сайт продлил")
        XCTAssertEqual(AccountGate.verdict(later, .revoked, now: now), .revoked)
        XCTAssertEqual(AccountGate.verdict(later, .unreachable, now: now), .keep(expiresAt: later, confirmed: false))
        XCTAssertEqual(AccountGate.verdict(earlier, .unreachable, now: now), .expired)
        XCTAssertTrue(AccountGate.allowed(later, now: now))
        XCTAssertFalse(AccountGate.allowed(now, now: now))
        XCTAssertFalse(AccountGate.allowed(nil, now: now))
    }

    // MARK: - Форма входа

    func testFormGoesToTheCodeStepAndBack() throws {
        var form = LoginForm()
        XCTAssertFalse(form.ready)
        form.setEmail(" admin@example.com ")
        form.setPassword("pw")
        XCTAssertTrue(form.canSubmit)
        let first = try XCTUnwrap(form.begin())
        XCTAssertNil(first.code)
        XCTAssertNil(first.recovery)
        XCTAssertNil(form.begin(), "запрос уже идет")
        form.answered(.codeRequired)
        XCTAssertTrue(form.codeStep)
        XCTAssertEqual(form.password, "pw", "пароль ждет кода")
        XCTAssertFalse(form.ready)
        form.setCode("1２3a45")
        XCTAssertEqual(form.code, "1345", "только цифры 0–9")
        form.setCode("123456789")
        XCTAssertEqual(form.code, "123456")
        let second = try XCTUnwrap(form.begin())
        XCTAssertEqual(second.code, "123456")
        XCTAssertNil(second.recovery)
        form.answered(.failed(.code))
        XCTAssertTrue(form.codeStep)
        XCTAssertEqual(form.error, .code)
        XCTAssertEqual(form.code, "")
        XCTAssertEqual(form.password, "pw")
        form.toggleRecovery()
        XCTAssertNil(form.error)
        form.setCode(" abcd-efgh ")
        let recovery = try XCTUnwrap(form.begin())
        XCTAssertEqual(recovery.recovery, " abcd-efgh ")
        XCTAssertNil(recovery.code)
        form.answered(.failed(.invalid))
        XCTAssertFalse(form.codeStep, "пароль сменили — снова первый шаг")
        XCTAssertEqual(form.password, "")
        XCTAssertEqual(form.error, .invalid)
        form.setPassword("pw")
        _ = form.begin()
        form.answered(.codeRequired)
        form.back()
        XCTAssertEqual(form, LoginForm(email: " admin@example.com "))
    }

    func testFirstStepFailureClearsThePassword() throws {
        var form = LoginForm(email: "user@example.com")
        form.setPassword("pw")
        _ = try XCTUnwrap(form.begin())
        form.answered(.failed(.rateLimited, retryMinutes: 3))
        XCTAssertEqual(form.error, .rateLimited)
        XCTAssertEqual(form.retryMinutes, 3)
        XCTAssertEqual(form.password, "")
        form.setPassword("pw")
        _ = form.begin()
        XCTAssertEqual(form.retryMinutes, 3, "минуты до ответа остаются")
        form.answered(.ok(AccountSession(token: "t", expiresAt: Date(), user: AccountUser(email: "e", displayName: ""), device: AccountDevice(id: 1, name: ""))))
        XCTAssertNil(form.error)
        XCTAssertEqual(form.password, "")
        XCTAssertEqual(form.retryMinutes, 0)
    }

    // MARK: - Хранилище входа

    func testStoreKeepsTheTokenOnlyInTheVault() throws {
        let file = MemoryFile()
        let vault = MemoryVault()
        let store = AccountStore(file: file, vault: vault)
        XCTAssertEqual(store.read(), AccountState())
        let uid = try store.deviceUid()
        XCTAssertTrue(DeviceInfo.validUid(uid))
        XCTAssertEqual(try store.deviceUid(), uid, "uid создается один раз")
        let now = Date(timeIntervalSince1970: 1_700_000_000)
        try store.save(session(expires: now.addingTimeInterval(86400)), now: now)
        let state = store.read()
        XCTAssertEqual(state.record?.email, "user@example.com")
        XCTAssertEqual(state.record?.deviceName, "iPhone · iOS 17.5")
        XCTAssertEqual(state.record?.checkedAt, now)
        XCTAssertTrue(state.allowed(now: now))
        XCTAssertFalse(state.allowed(now: now.addingTimeInterval(86400)))
        XCTAssertEqual(try store.token(), "tok-secret-1")
        let text = String(decoding: try XCTUnwrap(file.read()), as: UTF8.self)
        XCTAssertFalse(text.contains("tok-secret"), "токена нет в файле")
        XCTAssertTrue(store.clear(.revoked))
        XCTAssertEqual(store.read(), AccountState(record: nil, notice: .revoked))
        XCTAssertNil(try store.token())
        XCTAssertEqual(try store.deviceUid(), uid, "выход uid не стирает")
        try file.write(Data("мусор".utf8))
        XCTAssertEqual(store.read(), AccountState(), "испорченный файл — входа нет")
    }

    func testSaveFailureLeavesNoToken() {
        let vault = MemoryVault()
        let store = AccountStore(file: BrokenFile(), vault: vault)
        XCTAssertThrowsError(try store.save(session(expires: Date().addingTimeInterval(60)), now: Date()))
        XCTAssertNil(vault.all[AccountStore.tokenKey])
    }

    // MARK: - Сервис входа

    func testLoginSavesAndRefreshExtends() async throws {
        let clock = Clock(Date(timeIntervalSince1970: 1_700_000_000))
        let site = FakeSite()
        site.loginResult = .ok(session(expires: clock.now.addingTimeInterval(86400)))
        let store = AccountStore(file: MemoryFile(), vault: MemoryVault())
        let service = AccountService(store: store, source: site, device: { DeviceInfo(uid: $0, name: "iPhone", appVersion: "0.4.0") }, clock: { clock.now })
        let result = await service.login(email: "user@example.com", password: "pw", code: nil, recovery: nil)
        guard case .ok = result else { return XCTFail("ожидался вход") }
        XCTAssertEqual(site.devices.first?.uid, try store.deviceUid())
        let early = await service.refresh(force: false)
        XCTAssertNil(early, "сразу после входа сайт не спрашиваем снова")
        let extended = clock.now.addingTimeInterval(7 * 86400)
        site.statusResult = .ok(expiresAt: extended, user: AccountUser(email: "user@example.com", displayName: "Новое имя"), device: AccountDevice(id: 42, name: "iPhone"))
        clock.now += 120
        let confirmed = await service.refresh(force: false)
        XCTAssertEqual(confirmed, .keep(expiresAt: extended, confirmed: true))
        XCTAssertEqual(store.read().record?.expiresAt, extended)
        XCTAssertEqual(store.read().record?.displayName, "Новое имя")
        XCTAssertEqual(store.read().record?.checkedAt, clock.now)
        XCTAssertEqual(site.tokens, ["tok-secret-1"])
    }

    func testUnreachableSiteKeepsAccessUntilTheTermEnds() async throws {
        let clock = Clock(Date(timeIntervalSince1970: 1_700_000_000))
        let site = FakeSite()
        let store = AccountStore(file: MemoryFile(), vault: MemoryVault())
        try store.save(session(expires: clock.now.addingTimeInterval(3600)), now: clock.now)
        let service = AccountService(store: store, source: site, device: { DeviceInfo(uid: $0, name: "iPhone", appVersion: "1") }, clock: { clock.now })
        site.statusResult = .unreachable
        let kept = await service.refresh(force: true)
        XCTAssertEqual(kept, .keep(expiresAt: clock.now.addingTimeInterval(3600), confirmed: false))
        XCTAssertTrue(store.read().signedIn)
        clock.now += 3600
        let expired = await service.refresh(force: true)
        XCTAssertEqual(expired, .expired)
        XCTAssertEqual(store.read(), AccountState(record: nil, notice: .expired))
    }

    func testRevokedClearsTheSignIn() async throws {
        let store = AccountStore(file: MemoryFile(), vault: MemoryVault())
        try store.save(session(expires: Date().addingTimeInterval(3600)), now: Date())
        let site = FakeSite()
        site.statusResult = .revoked
        let service = AccountService(store: store, source: site, device: { DeviceInfo(uid: $0, name: "iPhone", appVersion: "1") })
        let revoked = await service.refresh(force: true)
        XCTAssertEqual(revoked, .revoked)
        XCTAssertEqual(store.read(), AccountState(record: nil, notice: .revoked))
        XCTAssertNil(try store.token())
    }

    func testExpiryNeedsNoNetwork() async throws {
        let clock = Clock(Date(timeIntervalSince1970: 1_700_000_000))
        let store = AccountStore(file: MemoryFile(), vault: MemoryVault())
        try store.save(session(expires: clock.now.addingTimeInterval(60)), now: clock.now)
        let service = AccountService(store: store, source: FakeSite(), device: { DeviceInfo(uid: $0, name: "iPhone", appVersion: "1") }, clock: { clock.now })
        let before = await service.expireIfDue()
        XCTAssertNil(before)
        clock.now += 60
        let after = await service.expireIfDue()
        XCTAssertEqual(after, .expired)
        XCTAssertEqual(store.read().notice, .expired)
    }

    func testAnswerAboutAnOldSignInIsDropped() async throws {
        let store = AccountStore(file: MemoryFile(), vault: MemoryVault())
        try store.save(session(expires: Date().addingTimeInterval(3600)), now: Date())
        let site = FakeSite()
        site.statusResult = .revoked
        let service = AccountService(store: store, source: site, device: { DeviceInfo(uid: $0, name: "iPhone", appVersion: "1") })
        site.onStatus = {
            // Пока сайт отвечал, участник вошел снова — с другим токеном.
            try? store.save(AccountSession(token: "tok-new", expiresAt: Date().addingTimeInterval(7200), user: AccountUser(email: "user@example.com", displayName: ""), device: AccountDevice(id: 42, name: "")), now: Date())
        }
        let stale = await service.refresh(force: true)
        XCTAssertNil(stale)
        XCTAssertEqual(try store.token(), "tok-new", "отзыв старого токена новый вход не стирает")
    }

    func testStoreFailureStopsTheSignIn() async {
        let site = FakeSite()
        site.loginResult = .ok(session(expires: Date().addingTimeInterval(3600)))
        let service = AccountService(store: AccountStore(file: BrokenFile(), vault: MemoryVault()), source: site, device: { DeviceInfo(uid: $0, name: "iPhone", appVersion: "1") })
        let broken = await service.login(email: "user@example.com", password: "pw", code: nil, recovery: nil)
        XCTAssertEqual(broken, .failed(.store))
        let locked = AccountService(store: AccountStore(file: MemoryFile(), vault: BrokenVault()), source: site, device: { DeviceInfo(uid: $0, name: "iPhone", appVersion: "1") })
        let lockedResult = await locked.login(email: "user@example.com", password: "pw", code: nil, recovery: nil)
        XCTAssertEqual(lockedResult, .failed(.store))
        XCTAssertEqual(site.devices.count, 1, "без Связки ключей сайт не спрашиваем")
    }

    func testConfirmNeedsTheSameAccount() async throws {
        let store = AccountStore(file: MemoryFile(), vault: MemoryVault())
        try store.save(session(expires: Date().addingTimeInterval(3600)), now: Date())
        let site = FakeSite()
        site.loginResult = .ok(session(expires: Date().addingTimeInterval(7200)))
        let service = AccountService(store: store, source: site, device: { DeviceInfo(uid: $0, name: "iPhone", appVersion: "1") })
        let other = await service.confirm(email: "other@example.com", password: "pw", code: nil)
        XCTAssertEqual(other, .failed(.invalid))
        XCTAssertTrue(site.devices.isEmpty, "другая почта — без запроса")
        guard case .ok = await service.confirm(email: " USER@example.com ", password: "pw", code: "123456") else {
            return XCTFail("ожидалось подтверждение")
        }

        XCTAssertEqual(site.codes, ["123456"])
        XCTAssertTrue(store.read().signedIn)
    }

    func testSignOutClearsFirstAndWaitsBriefly() async throws {
        let store = AccountStore(file: MemoryFile(), vault: MemoryVault())
        try store.save(session(expires: Date().addingTimeInterval(3600)), now: Date())
        let site = FakeSite()
        site.logoutDelay = 5
        let service = AccountService(store: store, source: site, device: { DeviceInfo(uid: $0, name: "iPhone", appVersion: "1") }, logoutWait: 0.2)
        let start = Date()
        await service.signOut()
        XCTAssertLessThan(Date().timeIntervalSince(start), 2, "выход не ждет сайт дольше предела")
        XCTAssertEqual(store.read(), AccountState())
        XCTAssertNil(try store.token())
        XCTAssertEqual(site.logouts, ["tok-secret-1"])
        let signedOut = await service.refresh(force: true)
        XCTAssertNil(signedOut, "входа нет — сайт не спрашиваем")
    }

    // MARK: - Помощники

    private func body(_ data: Data) throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func session(expires: Date) -> AccountSession {
        AccountSession(token: "tok-secret-1", expiresAt: expires, user: AccountUser(email: "user@example.com", displayName: "Участник"), device: AccountDevice(id: 42, name: "iPhone · iOS 17.5"))
    }
}

private final class Clock: @unchecked Sendable {
    var now: Date

    init(_ now: Date) {
        self.now = now
    }
}

/// Сайт для тестов: что ответить и что спросили.
private final class FakeSite: AccountSource, @unchecked Sendable {
    var loginResult = LoginResult.failed(.server)
    var statusResult = StatusResult.unreachable
    var logoutDelay: TimeInterval = 0
    var onStatus: (() -> Void)?
    var devices: [DeviceInfo] = []
    var codes: [String] = []
    var tokens: [String] = []
    var logouts: [String] = []

    func login(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo) async -> LoginResult {
        devices.append(device)
        codes += [code].compactMap { $0 }
        return loginResult
    }

    func status(token: String) async -> StatusResult {
        tokens.append(token)
        onStatus?()
        return statusResult
    }

    func logout(token: String) async -> Bool {
        logouts.append(token)
        try? await Task.sleep(nanoseconds: UInt64(logoutDelay * 1_000_000_000))
        return true
    }
}

private struct BrokenFile: StateFile {
    func read() throws -> Data? { throw CocoaError(.fileReadUnknown) }
    func write(_ data: Data) throws { throw CocoaError(.fileWriteUnknown) }
}

private struct BrokenVault: SecretVault {
    func store(_ secret: String, for key: String) throws { throw VaultError(status: -25308) }
    func load(_ key: String) throws -> String? { throw VaultError(status: -25308) }
    func remove(_ key: String) throws { throw VaultError(status: -25308) }
}
