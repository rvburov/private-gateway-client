import AVFoundation
import PhotosUI
import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UniformTypeIdentifiers
import VisionKit

/// «Добавить профиль» (доска 03, ImportPage десктопа) — шаг без островка: шапка с «Назад» стоит на месте, прокручиваются
/// четыре карточки источника и предпросмотр, внизу вне прокрутки — «Добавить N серверов». Источники: буфер обмена (системная
/// кнопка «Вставить» — без запроса доступа к буферу, решение 461), QR-код (камерой или с картинки из Фото — системный выбор,
/// без доступа ко всем фото), ссылка или адрес подписки https:// (поле и «Проверить» — только у выбранной карточки), файл
/// (ссылки, base64, sing-box, Clash). Предпросмотр — у выбранного источника: формат и протоколы, найдено, новых, дублей и
/// ошибок, строки с ошибками, название профиля. Без названия серверы идут в «Мои серверы», адрес подписки не хранится.
struct ImportView: View {
    @ObservedObject var model: ImportModel
    let texts: PgTexts
    let onClose: () -> Void

    @State private var photo: PhotosPickerItem?
    @State private var picking = false
    @State private var scanning = false
    @State private var choosingFile = false
    /// Где карточка QR — меню «Камера / Картинка» встает под ней.
    @State private var qrAnchor = UUID()
    @Environment(\.pgLayers) private var layers

    var body: some View {
        let batch = model.preview?.batch.flatMap { $0.found > 0 ? $0 : nil }
        ZStack {
            PgBackground()
            VStack(spacing: 0) {
                PgPageHeader(title: texts.pageAddTitle, texts: texts, fixed: true, onBack: onClose)
                ScrollView {
                    VStack(alignment: .leading, spacing: PgSpace.s) {
                        clipboard
                        qr
                        link
                        if model.source == .link {
                            linkBox
                        }

                        file
                        if let note = ImportLook.note(model, texts) {
                            Text(note.text)
                                .pgText(PgType.small)
                                .foregroundStyle(Color(note.warning ? PgColor.Status.Warning.text : PgColor.Text.secondary))
                                .fixedSize(horizontal: false, vertical: true)
                                .padding(.horizontal, PgSpace.xs)
                                .accessibilityIdentifier(note.identifier)
                        }

                        if let batch {
                            preview(batch).padding(.top, PgSpace.s)
                        }
                    }
                    .padding(.horizontal, PgSpace.windowSideMobile)
                    .padding(.bottom, PgSpace.card)
                }

                if let batch {
                    addButton(batch)
                }
            }
        }
        .onChange(of: photo) { item in
            guard let item else {
                return
            }

            photo = nil
            Task {
                if let data = try? await item.loadTransferable(type: Data.self) {
                    await model.readQr(data)
                } else {
                    await model.readQr(Data())
                }
            }
        }
        .onChange(of: model.text) { _ in
            model.edited()
        }
        .onChange(of: model.name) { name in
            if name.count > ServerNames.maxLength {
                model.name = String(name.prefix(ServerNames.maxLength))
            }
        }
        .photosPicker(isPresented: $picking, selection: $photo, matching: .images)
        .fileImporter(isPresented: $choosingFile, allowedContentTypes: ImportLook.fileTypes) { picked in
            Task { await model.readFile(picked) }
        }
        .sheet(isPresented: $scanning) {
            scanner
        }
        .accessibilityIdentifier("import")
    }

    /// «Из буфера обмена»: действие — системная «Вставить» справа (она отдает текст без запроса «Разрешить вставку»); сама
    /// карточка не кнопка — прочитать буфер по нажатию на нее iOS дала бы только с этим запросом.
    private var clipboard: some View {
        ImportSourceCard(
            icon: PgIcons.clipboard, title: texts.importClipboardTitle, caption: texts.importClipboardText, chosen: model.source == .clipboard,
            identifier: "import.source.clipboard",
            accessory: {
                PasteButton(payloadType: String.self) { strings in
                    Task { await model.paste(strings.first) }
                }
                .labelStyle(.iconOnly)
                .buttonBorderShape(.capsule)
                .tint(Color(PgColor.Accent.base))
                .accessibilityIdentifier("import.clipboard")
            }
        )
    }

    /// «QR-код»: камерой или с картинки — меню у карточки; iPhone без сканера VisionKit — сразу выбор картинки.
    private var qr: some View {
        ImportSourceCard(
            icon: PgIcons.qr, title: texts.androidImportQrTitle, caption: texts.iosImportQrText, chosen: model.source == .qr, identifier: "import.qr",
            action: { chooseQr() }
        )
        .pgMenuAnchor(qrAnchor)
    }

    private var link: some View {
        ImportSourceCard(
            icon: PgIcons.link, title: texts.importLinkTitle, caption: texts.importLinkText, chosen: model.source == .link, identifier: "import.link",
            action: { model.chooseLink() }
        )
    }

    /// Поле для ссылок по строкам или адреса подписки и «Проверить» справа — только у выбранной «Ввести ссылку».
    private var linkBox: some View {
        VStack(alignment: .trailing, spacing: PgSpace.s) {
            PgField(
                placeholder: texts.importLinkPlaceholder, text: $model.text, style: PgTextStyle(size: PgType.caption.size, weight: PgType.mono.weight, mono: true),
                multiline: true, identifier: "import.text"
            )
            .accessibilityLabel(texts.importLinkTitle)
            PgActionButton(
                label: texts.importLinkCheck, kind: .secondary, enabled: ImportLook.canCheck(model.text) && !model.downloading, identifier: "import.check"
            ) {
                Task { await model.check(model.text) }
            }
            .fixedSize(horizontal: true, vertical: false)
        }
    }

    private var file: some View {
        ImportSourceCard(
            icon: PgIcons.file, title: texts.importFileTitle, caption: texts.importFileText, chosen: model.source == .file, identifier: "import.file",
            action: {
                model.chooseFile()
                choosingFile = true
            }
        )
    }

    /// Предпросмотр (.im-prev): «Предпросмотр» и справа формат с протоколами; плитки «найдено», «новых», «дубли», «ошибки»;
    /// оранжевые плашки — строки с ошибками, http без шифрования, подписка уже добавлена; название профиля (у добавленной
    /// подписки его нет — она обновится в своей группе).
    private func preview(_ batch: ImportBatch) -> some View {
        let fresh = ImportLook.fresh(batch, count: model.count)
        let errors = ImportLook.errorLines(batch, texts)
        return VStack(alignment: .leading, spacing: PgSpace.l) {
            HStack(alignment: .firstTextBaseline, spacing: PgSpace.s) {
                Overline(text: texts.importPreview).fixedSize()
                Text(ImportLook.format(batch, texts))
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Text.muted))
                    .multilineTextAlignment(.trailing)
                    .frame(maxWidth: .infinity, alignment: .trailing)
                    .accessibilityIdentifier("import.format")
            }

            HStack(spacing: PgSpace.s) {
                tile(batch.found, texts.importFound, identifier: "import.found")
                tile(fresh, texts.importNew, tone: .good, identifier: "import.new")
                tile(model.count?.duplicates ?? 0, texts.importDuplicates, identifier: "import.duplicates")
                tile(batch.failed, texts.text(key: TextKeys.importErrors(batch.failed, texts.language.rawValue)) ?? "", tone: .bad, identifier: "import.errors")
            }

            if !errors.isEmpty {
                notice(errors, identifier: "import.lines")
            }

            if model.preview?.insecure == true {
                // Ключи серверов шли открытым текстом: их могли прочитать по пути.
                notice([texts.importInsecure], identifier: "import.insecure")
            }

            if let existing = model.existing {
                // Та же подписка уже добавлена: «Добавить» обновит ее, а не создаст вторую группу (решение 528).
                notice([texts.importSubscriptionExists(existing)], identifier: "import.existing")
            } else {
                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Text(texts.importName).pgText(PgType.small.with(weight: PgType.bodyStrong.weight)).foregroundStyle(Color(PgColor.Text.secondary))
                    PgField(placeholder: texts.importNamePlaceholder, text: $model.name, identifier: "import.name")
                        .accessibilityLabel(texts.importName)
                }
            }
        }
        .padding(.vertical, 14)
        .padding(.horizontal, PgSpace.card)
        .pgCard(padding: 0)
        .accessibilityIdentifier("import.preview")
    }

    private enum TileTone {
        case plain
        case good
        case bad
    }

    /// Плитка предпросмотра (.im-tile): число моноширинным размера status и подпись; новые — зеленым, ошибки — оранжевым.
    private func tile(_ number: Int, _ label: String, tone: TileTone = .plain, identifier: String) -> some View {
        let fill: PgRGBA
        let ink: PgRGBA
        let caption: PgRGBA
        switch tone {
        case .plain:
            fill = PgColor.Control.segmentTrack
            ink = PgColor.Text.primary
            caption = PgColor.Text.muted
        case .good:
            fill = PgColor.Status.Success.tint
            ink = PgColor.Status.Success.text
            caption = PgColor.Status.Success.badge
        case .bad:
            fill = PgColor.Status.Warning.tint
            ink = PgColor.Status.Warning.text
            caption = PgColor.Status.Warning.badge
        }

        return VStack(spacing: 2) {
            Text(String(number))
                .pgText(PgTextStyle(size: PgType.status.size, weight: 600, mono: true))
                .foregroundStyle(Color(ink))
                .lineLimit(1)
                .minimumScaleFactor(0.6)
            Text(label)
                .pgText(PgType.caption)
                .foregroundStyle(Color(caption))
                .lineLimit(1)
                .minimumScaleFactor(0.7)
        }
        .padding(.vertical, PgSpace.m)
        .padding(.horizontal, PgSpace.xxs)
        .frame(maxWidth: .infinity)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color(fill)))
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier(identifier)
    }

    /// Плашка предупреждения (.im-notice, Border.notice десктопа): warning.tint, рамка warning.line, текст small warning.badge.
    private func notice(_ lines: [String], identifier: String) -> some View {
        let shape = RoundedRectangle(cornerRadius: 12, style: .continuous)
        return VStack(alignment: .leading, spacing: 2) {
            ForEach(lines.indices, id: \.self) { index in
                Text(lines[index])
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Status.Warning.badge))
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.vertical, PgSpace.m)
        .padding(.horizontal, PgSpace.l)
        .background(shape.fill(Color(PgColor.Status.Warning.tint)))
        .overlay(shape.strokeBorder(Color(PgColor.Status.Warning.line), lineWidth: 1))
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier(identifier)
    }

    /// «Добавить N серверов» (.im-add): основная высотой 54 внизу экрана вне прокрутки; N — новых; новых нет — «Обновить защиту
    /// N серверов», иначе «Новых серверов нет» (недоступна).
    private func addButton(_ batch: ImportBatch) -> some View {
        let fresh = ImportLook.fresh(batch, count: model.count)
        let renewals = model.count?.renewals ?? 0
        return PgActionButton(
            label: ImportLook.addLabel(fresh: fresh, renewals: renewals, texts), enabled: ImportLook.canAdd(fresh: fresh, renewals: renewals) && !model.adding,
            identifier: "import.add", height: 54, style: PgType.title, radius: 16
        ) {
            Task { await model.add() }
        }
        .padding(.horizontal, PgSpace.windowSideMobile)
        .padding(.top, PgSpace.s)
        .padding(.bottom, PgSpace.windowSide)
    }

    private var scanner: some View {
        NavigationStack {
            QrScanner(
                onFound: { found in
                    scanning = false
                    Task { await model.scanned(found) }
                },
                onFailed: {
                    scanning = false
                    model.cameraProblem()
                }
            )
            .ignoresSafeArea()
            .navigationTitle(texts.androidImportQrCameraTitle)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(texts.androidImportQrCameraClose) { scanning = false }
                }
            }
        }
    }

    /// Нажатие на «QR-код»: карточка выбрана; камера есть — меню «Сканировать камерой» и «Выбрать картинку» у карточки, нет —
    /// сразу выбор картинки.
    private func chooseQr() {
        model.chooseQr()
        guard DataScannerViewController.isSupported, let layers else {
            picking = true
            return
        }

        layers.openMenu(
            PgMenu(
                anchor: qrAnchor, items: [PgMenuItem(title: texts.androidImportQrCamera), PgMenuItem(title: texts.androidImportQrImage)],
                onPick: { index in
                    if index == 0 {
                        openCamera()
                    } else {
                        picking = true
                    }
                }
            )
        )
    }

    /// Доступ к камере спрашивается по выбору «Сканировать камерой», а не при запуске; отказ — подсказка под источниками.
    private func openCamera() {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            scanning = true
        case .notDetermined:
            Task {
                if await AVCaptureDevice.requestAccess(for: .video) {
                    scanning = true
                } else {
                    model.cameraProblem()
                }
            }
        default:
            model.cameraProblem()
        }
    }
}

/// Карточка источника (.im-sources > .segment, Button.source десктопа): значок 22 на подложке accent.tint 44 × 44 со
/// скруглением 12, заголовок полужирным и подпись caption цветом text.muted; высота не меньше 66, скругление 16. Выбранная —
/// белая, рамка accent.base и тень chosen; иначе — surface.card и рамка line.border 1,5. Нажимается вся карточка (`action`);
/// без него карточка не кнопка, действие — элемент справа (`accessory`).
struct ImportSourceCard<Accessory: View>: View {
    let icon: String
    let title: String
    let caption: String
    let chosen: Bool
    var identifier = ""
    var action: (() -> Void)?
    @ViewBuilder let accessory: () -> Accessory

    var body: some View {
        if let action {
            Button(action: action) {
                face
            }
            .buttonStyle(PgPressStyle())
            .accessibilityAddTraits(chosen ? [.isButton, .isSelected] : .isButton)
            .accessibilityIdentifier(identifier)
        } else {
            face
                .accessibilityElement(children: .contain)
                .accessibilityAddTraits(chosen ? .isSelected : [])
                .accessibilityIdentifier(identifier)
        }
    }

    private var face: some View {
        let shape = RoundedRectangle(cornerRadius: 16, style: .continuous)
        return HStack(spacing: PgSpace.l) {
            PgIcon(data: icon, color: Color(PgColor.Accent.text), size: 22, lineWidth: 1.9)
                .frame(width: 44, height: 44)
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color(PgColor.Accent.tint)))
            VStack(alignment: .leading, spacing: 2) {
                Text(title).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                Text(caption).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted)).fixedSize(horizontal: false, vertical: true)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            accessory()
        }
        .padding(.horizontal, 14)
        .padding(.vertical, PgSpace.m)
        .frame(maxWidth: .infinity, minHeight: 66, alignment: .leading)
        .background(shape.fill(Color(chosen ? PgColor.Surface.solid : PgColor.Surface.card)))
        .overlay(shape.strokeBorder(Color(chosen ? PgColor.Accent.base : PgColor.Line.border), lineWidth: 1.5))
        .pgShadow(chosen ? PgShadow.chosen : [])
        .contentShape(Rectangle())
    }
}

extension ImportSourceCard where Accessory == EmptyView {
    /// Карточка без элемента справа — нажимается целиком.
    init(icon: String, title: String, caption: String, chosen: Bool, identifier: String = "", action: @escaping () -> Void) {
        self.init(icon: icon, title: title, caption: caption, chosen: chosen, identifier: identifier, action: action, accessory: { EmptyView() })
    }
}

/// Подпись под источниками: текст, предупреждение ли это (оранжевым) и метка для тестов.
struct ImportNote: Equatable {
    let text: String
    let warning: Bool
    let identifier: String
}

/// Правила экрана «Добавить профиль» — как ImportPageViewModel и ImportPreview десктопа.
enum ImportLook {
    /// Сколько строк с ошибками показывать по отдельности; остальные — одной строкой «И еще…».
    static let shownErrors = 3

    /// Что можно открыть «Открыть файлом»: текст, JSON и YAML, а также любой файл — подписки часто сохраняют без расширения;
    /// не текст скажет сам разбор.
    static var fileTypes: [UTType] {
        [.text, .json, .yaml, .data]
    }

    /// «Проверить» доступна, когда в поле есть текст.
    static func canCheck(_ text: String) -> Bool {
        !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// Новых — разобранных без дублей, посчитанных до добавления; не посчитано — все разобранные.
    static func fresh(_ batch: ImportBatch, count: ImportCount?) -> Int {
        max(0, batch.found - batch.failed - (count?.duplicates ?? 0))
    }

    /// Протоколы разобранных серверов в порядке фильтра списка серверов.
    static func protocols(_ batch: ImportBatch) -> [ProxyProtocol] {
        let present = Set(batch.lines.compactMap { $0.result.server?.protocol })
        return ServerList.protocolOrder.filter { present.contains($0) }
    }

    /// Короткие имена протоколов для строки формата, как в макете.
    static func shortName(_ proto: ProxyProtocol) -> String {
        switch proto {
        case .shadowsocks: return "SS"
        case .hysteria2: return "Hy2"
        case .wireGuard: return "WG"
        default: return ServerFacts.protocolName(proto)
        }
    }

    /// «base64 · VLESS / Hy2 / Trojan», «Clash · VLESS / Trojan».
    static func format(_ batch: ImportBatch, _ texts: PgTexts) -> String {
        let names = protocols(batch).map(shortName).joined(separator: " / ")
        let format = texts.text(key: TextKeys.importFormat(batch.format)) ?? batch.format
        return [format, names].filter { !$0.isEmpty }.joined(separator: " · ")
    }

    /// Строки плашки ошибок: первые три «Строка 4: неверный порт — пропущено.» и «И еще строк с ошибками: N.».
    static func errorLines(_ batch: ImportBatch, _ texts: PgTexts) -> [String] {
        let failed = batch.lines.filter { !$0.result.isOk }
        var lines = failed.prefix(shownErrors).map { line in
            texts.importLine(line.number, texts.text(key: TextKeys.importError(line.result.error)) ?? "")
        }
        if failed.count > shownErrors {
            lines.append(texts.importMore(failed.count - shownErrors))
        }

        return lines
    }

    /// Подпись «Добавить»: по числу новых; новых нет, но у сохраненных обновится защита (решение 484) — «Обновить защиту N
    /// серверов»; иначе — «Новых серверов нет».
    static func addLabel(fresh: Int, renewals: Int, _ texts: PgTexts) -> String {
        let language = texts.language.rawValue
        if fresh > 0 {
            return texts.text(key: TextKeys.importAdd(fresh, language), [String(fresh)]) ?? ""
        }

        if renewals > 0 {
            return texts.text(key: "import.renew." + Plural.form(renewals, language), [String(renewals)]) ?? ""
        }

        return texts.importNothing
    }

    /// «Добавить» доступна, когда есть что добавить или чью защиту обновить.
    static func canAdd(fresh: Int, renewals: Int) -> Bool {
        fresh > 0 || renewals > 0
    }

    /// Итог «Добавить» подсказкой: подписка уже была — «обновлена: новых N, удалено M»; иначе — сколько добавлено, у скольких
    /// обновлена защита или «Новых серверов нет».
    static func added(_ result: AddResult, existing: String?, _ texts: PgTexts) -> String {
        let language = texts.language.rawValue
        if let existing {
            return texts.subsUpdated(existing, result.added, result.removed)
        }

        if result.added > 0 {
            return texts.text(key: TextKeys.imported(result.added, language), [String(result.added)]) ?? ""
        }

        if result.renewed > 0 {
            return texts.text(key: "import.renewed." + Plural.form(result.renewed, language), [String(result.renewed)]) ?? ""
        }

        return texts.importNothing
    }

    /// Почему файл не прочитан — «Файл не прочитан: …».
    static func fileProblem(_ problem: ImportFileProblem, _ texts: PgTexts) -> String {
        let reason: String
        switch problem {
        case .tooLarge: reason = texts.text(key: "import.download.too_large") ?? ""
        case .notText: reason = texts.text(key: "import.error.bad_encoding") ?? ""
        case let .unreadable(detail): reason = detail
        }

        return texts.importFileFailed(reason)
    }

    /// Подпись под источниками (Message десктопа): идет загрузка или чтение картинки — серым; пустой буфер, QR не прочитан,
    /// файл не прочитан, подписка не загрузилась, серверов не найдено — оранжевым; сказать нечего — nil.
    @MainActor
    static func note(_ model: ImportModel, _ texts: PgTexts) -> ImportNote? {
        if model.downloading {
            return ImportNote(text: texts.importDownloading, warning: false, identifier: "import.downloading")
        }

        if model.qrReading {
            return ImportNote(text: texts.androidImportQrReading, warning: false, identifier: "import.qr.reading")
        }

        if model.emptyClipboard {
            return ImportNote(text: texts.importEmptyClipboard, warning: true, identifier: "import.empty.clipboard")
        }

        if let problem = model.qrProblem {
            return ImportNote(text: texts.text(key: problem) ?? "", warning: true, identifier: "import.qr.problem")
        }

        if let problem = model.fileProblem {
            return ImportNote(text: fileProblem(problem, texts), warning: true, identifier: "import.file.failed")
        }

        if let reason = model.preview?.failure {
            let failure = TextKeys.failure(reason)
            let because = texts.text(key: failure.key, [failure.value].compactMap { $0 }) ?? ""
            return ImportNote(text: texts.importDownloadFailed(because), warning: true, identifier: "import.failed")
        }

        if let batch = model.preview?.batch, batch.found == 0 {
            return ImportNote(text: texts.importEmptyText, warning: true, identifier: "import.empty")
        }

        return nil
    }
}
