import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UniformTypeIdentifiers

/// Файл копии для системных окон «Сохранить» и «Открыть»: текст .pgbackup.
struct BackupDocument: FileDocument {
    static var readableContentTypes: [UTType] { [.data, .json, .plainText] }

    var text: String

    init(text: String) {
        self.text = text
    }

    init(configuration: ReadConfiguration) throws {
        guard let data = configuration.file.regularFileContents else {
            throw BackupError.notBackup
        }

        text = String(decoding: data, as: UTF8.self)
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: Data(text.utf8))
    }
}

/// «Резервная копия» (доска 14, решение 559) — как `BackupScreen` Android: «Сохранить копию» — флажки, предупреждения, пароль
/// не короче 8 знаков, «Сохранить файл…» (окно «Сохранить» iOS); «Восстановить» — выбор файла, лист с паролем и тем, что в
/// копии; «Последняя автокопия» с вопросом «Вернуть автокопию?».
struct BackupView: View {
    let texts: PgTexts
    @ObservedObject var model: AppModel
    let onBack: () -> Void

    @Environment(\.pgLayers) private var layers
    @State private var parts = BackupParts()
    @State private var password = ""
    @State private var tried = false
    @State private var busy = false
    @State private var exporting = false
    @State private var document = BackupDocument(text: "")
    @State private var importing = false
    @State private var restoreText: String?
    @State private var sheetPassword = ""
    @State private var sheetError: String?
    @State private var opened: MobileBackup?
    @State private var restored: BackupRestored?
    @State private var lastAuto: Date?

    private static let minPassword = 8

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                PgPageHeader(title: texts.settingsAdvBackupTitle, texts: texts, onBack: onBack)
                save
                restore
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("backup")
        .onAppear { lastAuto = model.lastAutoCopy() }
        .fileExporter(isPresented: $exporting, document: document, contentType: .data, defaultFilename: BackupLook.fileName(Date())) { result in
            if case .success = result {
                password = ""
                tried = false
                layers?.show(texts.backupSaved)
            }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.data, .json, .plainText]) { result in
            guard case let .success(url) = result else {
                return
            }

            let access = url.startAccessingSecurityScopedResource()
            defer {
                if access { url.stopAccessingSecurityScopedResource() }
            }
            if let text = try? String(contentsOf: url, encoding: .utf8) {
                sheetPassword = ""
                sheetError = nil
                opened = nil
                restoreText = text
            } else {
                layers?.show(texts.backupErrorFile)
            }
        }
        .pgSheet(isPresented: Binding(get: { restoreText != nil }, set: { if !$0 { restoreText = nil } })) {
            restoreSheet
        }
    }

    private var save: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            Text(texts.backupSaveTitle).pgText(PgType.title).foregroundStyle(Color(PgColor.Text.primary))
            PgCheck(title: texts.backupServersTitle, isOn: parts.servers, identifier: "backup.servers") { on in
                parts.servers = on
                if !on { parts.keys = false }
            }
            PgCheck(title: texts.backupSettingsTitle, isOn: parts.settings, identifier: "backup.settings") { parts.settings = $0 }
            PgCheck(title: texts.backupKeysTitle, isOn: parts.keys, enabled: parts.servers, identifier: "backup.keys") { parts.keys = $0 }
            if parts.keys {
                warning(texts.backupKeysWarning)
            } else if parts.servers {
                warning(texts.backupSubscriptionsWarning)
            }

            Text(texts.backupPassword).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary)).padding(.top, PgSpace.s)
            SecureField("", text: $password)
                .pgText(PgType.body)
                .padding(.horizontal, PgSpace.l)
                .frame(minHeight: PgLayout.touchTarget)
                .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(PgColor.Surface.solid)))
                .overlay(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).strokeBorder(Color(PgColor.Line.strong), lineWidth: 1))
                .accessibilityLabel(texts.backupPassword)
                .accessibilityIdentifier("backup.password")
            if tried, password.count < Self.minPassword {
                Text(texts.backupPasswordShort(Self.minPassword)).pgText(PgType.caption).foregroundStyle(Color(PgColor.Status.Danger.text))
            } else {
                Text(texts.backupPasswordNote).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
            }

            PgActionButton(label: texts.backupSave, enabled: !busy, identifier: "backup.save", height: 48) {
                tried = true
                guard parts.servers || parts.settings else {
                    layers?.show(texts.backupSaveNothing)
                    return
                }

                guard password.count >= Self.minPassword else {
                    return
                }

                busy = true
                Task {
                    defer { busy = false }
                    if let text = try? await model.backupCreate(parts, password: password) {
                        document = BackupDocument(text: text)
                        exporting = true
                    } else {
                        layers?.show(texts.backupErrorFile)
                    }
                }
            }
            .padding(.top, PgSpace.s)
        }
        .pgCard()
    }

    private var restore: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            Text(texts.backupRestoreTitle).pgText(PgType.title).foregroundStyle(Color(PgColor.Text.primary))
            Text(texts.backupRestoreNoteMobile).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
            PgActionButton(label: texts.backupRestoreChoose, kind: .secondary, identifier: "backup.choose", height: 48) { importing = true }
            if let restored {
                Text(BackupLook.restored(restored, texts)).pgText(PgType.small).foregroundStyle(Color(PgColor.Status.Success.text))
                    .accessibilityIdentifier("backup.restored")
            }

            HStack(spacing: PgSpace.m) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(texts.backupAutoTitle).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                    Text(lastAuto.map { texts.backupAutoMeta(BackupLook.date($0)) } ?? texts.backupAutoNone)
                        .pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
                }
                Spacer(minLength: 0)
                if lastAuto != nil {
                    PgActionButton(label: texts.backupAutoRestore, kind: .secondary, identifier: "backup.auto.restore") {
                        layers?.confirm(BackupLook.autoDialog(texts)) {
                            Task {
                                do {
                                    restored = try await model.backupRestoreAuto()
                                } catch {
                                    layers?.show(texts.backupErrorAutocopy)
                                }
                                lastAuto = model.lastAutoCopy()
                            }
                        }
                    }
                    .fixedSize()
                }
            }
            .padding(.top, PgSpace.s)
        }
        .pgCard()
    }

    @ViewBuilder
    private var restoreSheet: some View {
        PgSheetHeader(title: texts.backupSheetTitle, closeLabel: texts.cardClose, identifier: "backup.sheet.close") { restoreText = nil }
        VStack(alignment: .leading, spacing: PgSpace.m) {
            if let copy = opened {
                Text(BackupLook.summary(copy, texts)).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.primary))
                    .accessibilityIdentifier("backup.sheet.summary")
                Text(texts.backupRestoreNoteMobile).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
                PgActionButton(label: texts.backupSheetRestore, enabled: !busy, identifier: "backup.sheet.restore", height: 50) {
                    busy = true
                    Task {
                        defer { busy = false }
                        restored = try? await model.backupRestore(copy)
                        lastAuto = model.lastAutoCopy()
                        restoreText = nil
                    }
                }
            } else {
                Text(texts.backupPassword).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                SecureField("", text: $sheetPassword)
                    .pgText(PgType.body)
                    .padding(.horizontal, PgSpace.l)
                    .frame(minHeight: PgLayout.touchTarget)
                    .overlay(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).strokeBorder(Color(PgColor.Line.strong), lineWidth: 1))
                    .accessibilityLabel(texts.backupPassword)
                    .accessibilityIdentifier("backup.sheet.password")
                if let sheetError {
                    Text(sheetError).pgText(PgType.caption).foregroundStyle(Color(PgColor.Status.Danger.text))
                }

                PgActionButton(label: texts.backupSheetOpen, enabled: !busy && !sheetPassword.isEmpty, identifier: "backup.sheet.open", height: 50) {
                    guard let text = restoreText else {
                        return
                    }

                    busy = true
                    Task {
                        defer { busy = false }
                        do {
                            opened = try await model.backupOpen(text, password: sheetPassword)
                        } catch let error as BackupError {
                            sheetError = BackupLook.error(error, texts)
                        } catch {
                            sheetError = texts.backupErrorNotBackup
                        }
                    }
                }
            }
        }
        .padding(.top, PgSpace.l)
    }

    private func warning(_ text: String) -> some View {
        Text(text)
            .pgText(PgType.caption)
            .foregroundStyle(Color(PgColor.Status.Warning.text))
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, PgSpace.m)
            .padding(.vertical, PgSpace.s)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color(PgColor.Status.Warning.tint)))
    }
}

/// Правила экрана копии — тексты и вопрос.
enum BackupLook {
    static func fileName(_ at: Date) -> String {
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.dateFormat = "yyyy-MM-dd"
        return "private-gateway-" + format.string(from: at) + ".pgbackup"
    }

    static func date(_ at: Date) -> String {
        DateFormatter.localizedString(from: at, dateStyle: .medium, timeStyle: .short)
    }

    /// «Копия от …: серверы и подписки (7, с ключами), настройки.»
    static func summary(_ copy: MobileBackup, _ texts: PgTexts) -> String {
        var parts: [String] = []
        if let count = copy.servers?.count ?? copy.subscriptions?.count {
            parts.append(copy.keys ? texts.backupPartServersKeys(count) : texts.backupPartServersNoKeys(count))
        }

        if copy.settings != nil {
            parts.append(texts.backupPartSettings)
        }

        return texts.backupSummary(date(copy.createdAt), parts.joined(separator: ", "))
    }

    /// «Восстановлено: 3 сервера, 2 подписки, настройки».
    static func restored(_ done: BackupRestored, _ texts: PgTexts) -> String {
        let language = texts.language == .russian ? "ru" : "en"
        var parts: [String] = []
        if done.servers > 0 {
            parts.append(texts.text(key: "backup.count." + TextKeys.form(done.servers, language), [String(done.servers)]) ?? String(done.servers))
        }

        if done.subscriptions > 0 {
            parts.append(texts.text(key: "backup.subs." + TextKeys.form(done.subscriptions, language), [String(done.subscriptions)]) ?? String(done.subscriptions))
        }

        if done.settings {
            parts.append(texts.backupPartSettings)
        }

        return texts.backupRestored(parts.joined(separator: ", "))
    }

    static func error(_ error: BackupError, _ texts: PgTexts) -> String {
        switch error {
        case .wrongPassword: return texts.backupErrorPassword
        case .newerVersion: return texts.backupErrorNewer
        case .fromComputer: return texts.backupErrorFromComputer
        case .notBackup: return texts.backupErrorNotBackup
        }
    }

    /// «Вернуть автокопию?»: основная — «Отмена», вторая — «Вернуть».
    static func autoDialog(_ texts: PgTexts) -> PgDialog {
        PgDialog(
            kind: .info, icon: PgIcons.advBackup, title: texts.dialogAutocopyTitle, text: texts.dialogAutocopyText, primary: texts.dialogCancel,
            secondary: texts.dialogAutocopyRestore
        )
    }
}
