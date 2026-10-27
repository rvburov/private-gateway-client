import Foundation

/// Что показать на экране «Добавить» после проверки текста — как `ImportUi` Android: разбор (формат, строки), адрес
/// подписки, если текст — адрес и подписка загрузилась (секрет — живет только в памяти окна), шли ли ключи без шифрования
/// (http), почему не загрузилось и какое название предложить.
public struct ImportPreview: Equatable, Sendable {
    public var batch: ImportBatch?
    public var url: URLComponents?
    public var insecure: Bool
    public var failure: FailureReason?
    /// Название из Profile-Title или сервер подписки; nil — не предлагать.
    public var suggestedName: String?

    public init(batch: ImportBatch? = nil, url: URLComponents? = nil, insecure: Bool = false, failure: FailureReason? = nil, suggestedName: String? = nil) {
        self.batch = batch
        self.url = url
        self.insecure = insecure
        self.failure = failure
        self.suggestedName = suggestedName
    }
}

/// Добавление серверов — `preview` и `add` `MainViewModel` Android: ссылки, base64, sing-box или Clash — тем же разбором, что
/// у десктопа (shared/testdata); одна строка с адресом http(s) — подписка: сначала загрузить, потом разобрать ответ.
public enum ImportFlow {
    public static func preview(_ text: String, source: some SubscriptionSource) async -> ImportPreview {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.contains("\n"), let url = SubscriptionAddress.parse(trimmed) else {
            return ImportPreview(batch: ImportParser.parse(text))
        }

        do {
            let content = try await source.download(url)
            return ImportPreview(
                batch: ImportParser.parse(content.text), url: url, insecure: SubscriptionAddress.insecure(url),
                suggestedName: SubscriptionFetch.suggestName(content.title, url)
            )
        } catch let error as SubscriptionDownloadError {
            return ImportPreview(failure: FailureReason.of(error.failure, status: error.status))
        } catch {
            return ImportPreview(failure: .network)
        }
    }

    /// Добавить разобранные серверы: с названием — своим профилем (подпиской, если загружено по адресу), без названия — в
    /// «Мои серверы», адрес подписки не хранится. Ничего не было выбрано — выбрать первый новый, иначе выбор не меняется.
    @discardableResult
    public static func add(_ batch: ImportBatch, name: String, url: URLComponents?, servers: ServerStore, settings: SettingsStore) async throws -> AddResult {
        let parsed = batch.lines.compactMap(\.result.server)
        let before = Set(try await servers.servers().map(\.id))
        let result = try await servers.add(parsed, group: name, url: url)
        if try await settings.load().selectedServerId == nil, let first = try await servers.servers().first(where: { !before.contains($0.id) }) {
            try await settings.select(first.id)
        }

        return result
    }
}
