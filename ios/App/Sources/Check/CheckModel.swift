import Foundation
import PrivateGatewayCore

/// Что «Проверка» узнала сама, сверх состояния туннеля: идет ли проверка, идет ли замер задержки, последняя задержка, прокси
/// и VPN других приложений.
struct CheckFindings: Equatable {
    var running = false
    var measuringDelay = false
    var delay: DelayBreakdown?
    var proxy = ProxyFindings()
}

/// «Проверка» — как `CheckPageViewModel` десктопа, в пределах телефона: «Проверить» читает прокси системы и VPN других
/// приложений и меряет внешний IP заново (строки ждут, пока это идет), затем — задержку. Нажатие во время проверки — еще одна
/// проверка сразу после текущей. Проверки только читают состояние и ничего не меняют. Задержка — только под защитой: до сервера
/// (расширение туннеля), через сервер и новое соединение (`DelayProbe`).
@MainActor
final class CheckModel: ObservableObject {
    typealias Measure = @MainActor () async -> Int?

    @Published private(set) var findings = CheckFindings()

    private let through: Measure
    private let fresh: Measure
    private let readProxy: @MainActor () -> ProxyFindings
    private var rerun = false
    private var generation: UInt64 = 0
    private var delayAgain = false
    private var delayConnected = false
    private var delayServer: Measure?

    func invalidate() {
        generation &+= 1
        findings.delay = nil
    }

    init(
        through: @escaping Measure = { await DelayProbe.throughServer() }, fresh: @escaping Measure = { await DelayProbe.newConnection() },
        proxy: @escaping @MainActor () -> ProxyFindings = { SystemProxy.read() }
    ) {
        self.through = through
        self.fresh = fresh
        readProxy = proxy
    }

    /// «Проверить» (и открытие экрана, и смена состояния защиты): прокси и VPN других приложений, внешний IP — пока это идет,
    /// строки «Проверяется»; потом задержка.
    /// - Parameters:
    ///   - connected: защита подключена — спрашивается, когда дело доходит до задержки.
    ///   - measureIp: замер внешнего IP, если сейчас есть что мерить (`RootView`).
    ///   - toServer: «До сервера» от расширения туннеля.
    func run(connected: @MainActor () -> Bool, measureIp: @MainActor () async -> Void, toServer: @escaping Measure) async {
        guard !findings.running else {
            rerun = true
            return
        }

        findings.running = true
        repeat {
            rerun = false
            findings.proxy = readProxy()
            await measureIp()
        } while rerun

        findings.running = false
        await measureDelay(connected: connected(), toServer: toServer)
    }

    /// «Обновить» у «Задержки»: три числа по три замера; без защиты мерить через сервер нечего — чисел нет. Замер уже идет —
    /// повтор ставится после текущего; старый сеанс не выдает свой результат за новый.
    func measureDelay(connected: Bool, toServer: @escaping Measure) async {
        delayConnected = connected
        guard connected else {
            invalidate()
            return
        }
        delayServer = toServer

        guard !findings.measuringDelay else {
            delayAgain = true
            return
        }

        findings.measuringDelay = true
        defer { delayServer = nil }
        repeat {
            delayAgain = false
            guard delayConnected, !Task.isCancelled else { break }
            let current = generation
            let server = await delayServer?()
            let warm = await through()
            let cold = await fresh()
            if current == generation, !Task.isCancelled, delayConnected {
                findings.delay = DelayBreakdown(toServer: server, through: warm, fresh: cold, measuredAt: Date())
            }
        } while delayAgain
        findings.measuringDelay = false
    }
}
