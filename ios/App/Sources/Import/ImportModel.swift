import Foundation
import PrivateGatewayCore

/// Откуда взят текст — выбранная карточка источника (ImportSource десктопа): у нее белый фон, синяя рамка, и предпросмотр —
/// ее.
enum ImportSource: Equatable, Sendable {
    case none
    case clipboard
    case qr
    case link
    case file
}

/// Почему файл не прочитан: больше предела, не текст UTF-8 или ошибка iOS (ее текст — уже на языке телефона).
enum ImportFileProblem: Equatable, Sendable {
    case tooLarge
    case notText
    case unreadable(String)
}

/// Прочитанный файл: текст или почему не прочитан.
enum ImportFileRead: Equatable, Sendable {
    case text(String)
    case failed(ImportFileProblem)
}

/// Экран «Добавить профиль» (доска 03, ImportPageViewModel десктопа) — состояние и действия: выбранный источник (буфер, QR,
/// ссылка, файл), проверка текста (адрес подписки сначала загружается), предпросмотр — сколько найдено, новых, дублей и
/// ошибок (новые и дубли считаются до добавления, как у десктопа), название профиля, добавление. Адрес подписки — секрет:
/// живет только здесь, в памяти окна. Новый источник делает прежний предпросмотр и все ожидающие ответы неактуальными.
@MainActor
final class ImportModel: ObservableObject {
    /// Файл больше этого — не список серверов; предел — как у загрузки подписки (текст «подписка больше 5 МБ» — тот же).
    nonisolated static let maxFileBytes = DownloadLimits.maxBytes

    @Published var text = ""
    @Published var name = ""
    @Published private(set) var source = ImportSource.none
    @Published private(set) var preview: ImportPreview?
    /// Дубли и обновления защиты среди разобранного — до добавления; nil — не посчитано (тогда все разобранное — новые).
    @Published private(set) var count: ImportCount?
    @Published private(set) var downloading = false
    @Published private(set) var adding = false
    /// Сколько добавлено; nil — еще не добавляли.
    @Published private(set) var added: Int?
    @Published private(set) var duplicates = 0
    @Published private(set) var emptyClipboard = false
    @Published private(set) var qrReading = false
    /// Почему QR не прочитан — ключ текста.
    @Published private(set) var qrProblem: String?
    @Published private(set) var fileProblem: ImportFileProblem?
    /// Эта подписка уже добавлена — ее название: «Добавить» обновит ее, а не создаст вторую группу (решение 528).
    @Published private(set) var existing: String?

    private let container: SharedContainer
    private let downloader: any SubscriptionSource
    private var attempt = 0

    /// Серверы добавлены: итог и название подписки, если она уже была и обновилась. Окно перечитывает список, показывает итог
    /// подсказкой и ведет на «Серверы».
    var onAdded: (AddResult, String?) -> Void = { _, _ in }

    init(container: SharedContainer, source: any SubscriptionSource = SubscriptionDownloader()) {
        self.container = container
        downloader = source
    }

    /// Разобранные серверы предпросмотра — без строк с ошибками.
    var parsed: [ServerDefinition] {
        preview?.batch?.lines.compactMap(\.result.server) ?? []
    }

    /// «Ввести ссылку»: карточка выбрана, поле и «Проверить» раскрыты; предпросмотр другого источника уходит. Уже выбрана —
    /// ничего: введенное и проверенное остается.
    func chooseLink() {
        guard source != .link else {
            return
        }

        begin(.link)
    }

    /// «QR-код»: карточка выбрана, дальше — камера или картинка.
    func chooseQr() {
        begin(.qr)
    }

    /// «Открыть файл»: карточка выбрана, дальше — выбор файла iOS.
    func chooseFile() {
        begin(.file)
    }

    /// Текст поля изменили после проверки: прежний предпросмотр уже не о нем — кнопка обещала бы не то, что добавит.
    func edited() {
        guard source == .link, preview != nil || downloading else {
            return
        }

        begin(.link)
    }

    /// «Проверить»: ссылки, base64, sing-box, Clash или адрес подписки из поля.
    func check(_ text: String) async {
        let mine = begin(.link)
        await analyze(text, suggestedName: nil, attempt: mine)
    }

    /// Вставленный текст (кнопка «Вставить» системы — без запроса доступа к буферу) — сразу на проверку; в поле ссылки он не
    /// попадает.
    func paste(_ pasted: String?) async {
        let mine = begin(.clipboard)
        guard let pasted, !pasted.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            emptyClipboard = true
            return
        }

        await analyze(pasted, suggestedName: nil, attempt: mine)
    }

    /// QR-код с картинки: найденный текст разбирается как введенный, но в поле не попадает — в нем мог быть свой текст.
    func readQr(_ data: Data) async {
        let mine = begin(.qr)
        qrReading = true
        let read = await Task.detached { () -> Result<String?, any Error> in
            Result { try QrCode.read(data) }
        }.value
        guard mine == attempt else {
            return
        }

        qrReading = false
        switch read {
        case let .success(found?):
            await analyze(found, suggestedName: nil, attempt: mine)
        case .success(nil):
            qrProblem = "android.import.qr.none"
        case .failure:
            qrProblem = "android.import.qr.failed"
        }
    }

    /// Код с камеры — как введенный текст; ответ картинки, выбранной раньше, отбрасывается.
    func scanned(_ found: String) async {
        let mine = begin(.qr)
        await analyze(found, suggestedName: nil, attempt: mine)
    }

    /// Камера не открылась или к ней нет доступа.
    func cameraProblem() {
        begin(.qr)
        qrProblem = "ios.import.qr.camera.failed"
    }

    /// Файл из выбора iOS: текст UTF-8 не больше `maxFileBytes` — разбор тем же `ImportParser`; название профиля — имя файла
    /// без расширения (у десктопа так же).
    func readFile(_ picked: Result<URL, any Error>) async {
        let mine = begin(.file)
        let url: URL
        switch picked {
        case let .success(chosen):
            url = chosen
        case let .failure(error):
            fileProblem = .unreadable(error.localizedDescription)
            return
        }

        let read = await Task.detached { ImportModel.read(url) }.value
        guard mine == attempt else {
            return
        }

        switch read {
        case let .text(content):
            await analyze(content, suggestedName: url.deletingPathExtension().lastPathComponent, attempt: mine)
        case let .failed(problem):
            fileProblem = problem
        }
    }

    /// Текст файла из выбора iOS (доступ к нему — только на время чтения); больше предела или не UTF-8 — почему нет.
    nonisolated static func read(_ url: URL) -> ImportFileRead {
        let scoped = url.startAccessingSecurityScopedResource()
        defer {
            if scoped {
                url.stopAccessingSecurityScopedResource()
            }
        }

        if let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size > maxFileBytes {
            return .failed(.tooLarge)
        }

        let data: Data
        do {
            data = try Data(contentsOf: url)
        } catch {
            return .failed(.unreadable(error.localizedDescription))
        }

        guard data.count <= maxFileBytes else {
            return .failed(.tooLarge)
        }

        guard let text = String(data: data, encoding: .utf8) else {
            return .failed(.notText)
        }

        return .text(text)
    }

    /// Список серверов изменился, пока предпросмотр открыт (обновление подписки, удаление): дубли и «уже добавлена» —
    /// заново, иначе кнопка обещала бы не то, что сделает добавление.
    func recount() async {
        guard let preview, preview.batch != nil, added == nil, !adding else {
            return
        }

        let mine = attempt
        let fresh = await counted(parsed, url: preview.url)
        guard mine == attempt else {
            return
        }

        count = fresh.count
        existing = fresh.existing
    }

    /// Добавить разобранное. Повторное нажатие, пока идет добавление, не считается: второе добавление нашло бы одни дубли и
    /// затерло итог первого. Не сохранилось (Связка ключей недоступна) — экран остается, кнопку можно нажать снова.
    func add() async {
        guard let preview, let batch = preview.batch, added == nil, !adding else {
            return
        }

        adding = true
        let result = try? await ImportFlow.add(batch, name: name, url: preview.url, servers: container.servers, settings: container.settings)
        adding = false
        guard self.preview == preview, let result else {
            return
        }

        added = result.added
        duplicates = result.duplicates
        onAdded(result, existing)
    }

    /// Закрыть экран: источник, текст, адрес подписки и итог — забыть.
    func reset() {
        begin(.none)
        text = ""
    }

    /// Новый ввод: прежний предпросмотр и все ожидающие ответы неактуальны, название — заново (BeginOperation десктопа).
    @discardableResult
    private func begin(_ next: ImportSource) -> Int {
        attempt += 1
        source = next
        preview = nil
        count = nil
        existing = nil
        name = ""
        downloading = false
        adding = false
        added = nil
        duplicates = 0
        emptyClipboard = false
        qrReading = false
        qrProblem = nil
        fileProblem = nil
        return attempt
    }

    /// Разобрать текст источника: одна строка с адресом http(s) — подписка, ее сначала загрузить; потом — дубли и «уже
    /// добавлена». Предпросмотр, числа и название показываются вместе, когда все посчитано.
    private func analyze(_ text: String, suggestedName: String?, attempt mine: Int) async {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        downloading = !trimmed.contains("\n") && SubscriptionAddress.parse(trimmed) != nil
        let result = await ImportFlow.preview(text, source: downloader)
        guard mine == attempt else {
            return
        }

        let servers = result.batch?.lines.compactMap(\.result.server) ?? []
        let fresh = await counted(servers, url: result.url)
        guard mine == attempt else {
            return
        }

        downloading = false
        preview = result
        count = fresh.count
        existing = fresh.existing
        name = suggestedName ?? result.suggestedName ?? ""
    }

    /// Дубли и обновления защиты среди серверов и название подписки с этим адресом, если она уже добавлена.
    private func counted(_ servers: [ServerDefinition], url: URLComponents?) async -> (count: ImportCount?, existing: String?) {
        let count = try? await container.servers.countImport(servers)
        var existing: String?
        if let url, let id = try? await container.servers.subscriptionFor(url) {
            existing = try? await container.servers.groups().first { $0.id == id }?.name
        }

        return (count, existing)
    }
}
