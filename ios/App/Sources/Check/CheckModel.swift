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
    func run(connected: @MainActor () -> Bool, measureIp: @MainActor () async -> Void, toServer: Measure) async {
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
    /// второй не начинается.
    func measureDelay(connected: Bool, toServer: Measure) async {
        guard connected else {
            findings.delay = nil
            return
        }

        guard !findings.measuringDelay else {
            return
        }

        findings.measuringDelay = true
        let server = await toServer()
        let warm = await through()
        let cold = await fresh()
        findings.delay = DelayBreakdown(toServer: server, through: warm, fresh: cold, measuredAt: Date())
        findings.measuringDelay = false
    }
}
