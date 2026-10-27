import Foundation
import PrivateGatewayCore

/// Экран «Добавить» — состояние и действия, как `ImportUi` и импорт `MainViewModel` Android: текст и его проверка (адрес
/// подписки сначала загружается), предпросмотр, название профиля, добавление и его итог, QR с картинки. Адрес подписки —
/// секрет: живет только здесь, в памяти окна. Ответ на прежнюю проверку, пришедший после новой, отбрасывается.
@MainActor
final class ImportModel: ObservableObject {
    @Published var text = ""
    @Published var name = ""
    @Published private(set) var preview: ImportPreview?
    @Published private(set) var downloading = false
    @Published private(set) var adding = false
    /// Сколько добавлено; nil — еще не добавляли.
    @Published private(set) var added: Int?
    @Published private(set) var duplicates = 0
    @Published private(set) var emptyClipboard = false
    @Published private(set) var qrReading = false
    /// Почему QR не прочитан — ключ текста.
    @Published private(set) var qrProblem: String?

    private let container: SharedContainer
    private let source: any SubscriptionSource
    private var attempt = 0

    /// Серверы добавлены — окно перечитывает список.
    var onAdded: () -> Void = {}

    init(container: SharedContainer, source: any SubscriptionSource = SubscriptionDownloader()) {
        self.container = container
        self.source = source
    }

    /// Проверить текст: ссылки, base64, sing-box, Clash или адрес подписки.
    func check(_ text: String) async {
        attempt += 1
        let mine = attempt
        preview = nil
        added = nil
        qrProblem = nil
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        downloading = !trimmed.contains("\n") && SubscriptionAddress.parse(trimmed) != nil
        let result = await ImportFlow.preview(text, source: source)
        guard mine == attempt else {
            return
        }

        downloading = false
        preview = result
        if name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, let suggested = result.suggestedName {
            name = suggested
        }
    }

    /// Вставленный текст (кнопка «Вставить» системы — без запроса доступа к буферу): в поле и сразу на проверку.
    func paste(_ pasted: String?) async {
        guard let pasted, !pasted.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            emptyClipboard = true
            return
        }

        emptyClipboard = false
        text = pasted
        await check(pasted)
    }

    /// QR-код с картинки: найденный текст разбирается как введенный, но в поле не попадает — в нем мог быть свой текст.
    func readQr(_ data: Data) async {
        attempt += 1
        let mine = attempt
        qrReading = true
        qrProblem = nil
        let read = await Task.detached { () -> Result<String?, any Error> in
            Result { try QrCode.read(data) }
        }.value
        guard mine == attempt else {
            return
        }

        qrReading = false
        switch read {
        case let .success(found?):
            await check(found)
        case .success(nil):
            qrProblem = "android.import.qr.none"
        case .failure:
            qrProblem = "android.import.qr.failed"
        }
    }

    /// Код с камеры — как введенный текст; ответ картинки, выбранной раньше, отбрасывается.
    func scanned(_ found: String) async {
        await check(found)
    }

    /// Камера не открылась или к ней нет доступа.
    func cameraProblem() {
        attempt += 1
        qrReading = false
        qrProblem = "ios.import.qr.camera.failed"
    }

    /// Добавить разобранное. Повторное нажатие, пока идет добавление, не считается: второе добавление нашло бы одни дубли и
    /// затерло итог первого.
    func add() async {
        guard let preview, let batch = preview.batch, added == nil, !adding else {
            return
        }

        adding = true
        let result = try? await ImportFlow.add(batch, name: name, url: preview.url, servers: container.servers, settings: container.settings)
        adding = false
        guard self.preview == preview else {
            return
        }

        added = result?.added ?? 0
        duplicates = result?.duplicates ?? 0
        onAdded()
    }

    /// Закрыть экран: текст, адрес подписки и итог — забыть.
    func reset() {
        attempt += 1
        text = ""
        name = ""
        preview = nil
        downloading = false
        adding = false
        added = nil
        duplicates = 0
        emptyClipboard = false
        qrReading = false
        qrProblem = nil
    }
}
