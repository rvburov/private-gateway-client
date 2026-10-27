import AVFoundation
import PhotosUI
import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import VisionKit

/// «Добавить» (доска 3, для телефона) — как `ImportScreen` Android: текст из буфера обмена (системная кнопка «Вставить» —
/// без запроса доступа к буферу) или введенный — ссылки, base64, sing-box, Clash или адрес подписки https://; QR камерой или
/// с картинки из Фото (системный выбор — без доступа ко всем фото); предпросмотр с форматом, числом серверов и пропущенными
/// строками, название профиля — и только потом добавление. Без названия серверы идут в «Мои серверы», адрес подписки не
/// хранится.
struct ImportView: View {
    @ObservedObject var model: ImportModel
    let texts: PgTexts
    let onClose: () -> Void

    @State private var photo: PhotosPickerItem?
    @State private var scanning = false

    /// Сколько строк с ошибками показывать по отдельности; остальные — одной строкой «И еще…».
    private static let shownErrors = 5

    var body: some View {
        ZStack {
            PgBackground()
            ScrollView {
                VStack(alignment: .leading, spacing: PgSpace.l) {
                    PgPageHeader(title: texts.headerAdd, texts: texts, onBack: onClose)
                    clipboard
                    qr
                    link
                    if let batch = model.preview?.batch {
                        preview(batch)
                    }
                }
                .padding(.horizontal, PgSpace.windowSideMobile)
                .padding(.bottom, PgSpace.l)
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
        .sheet(isPresented: $scanning) {
            scanner
        }
        .accessibilityIdentifier("import")
    }

    private var clipboard: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            HStack(spacing: PgSpace.l) {
                PgIcon(data: PgIcons.clipboard, color: Color(PgColor.Accent.base), size: 22)
                VStack(alignment: .leading, spacing: 2) {
                    Text(texts.importClipboardTitle).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                    Text(texts.importClipboardText).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
                }

                Spacer(minLength: 0)
                PasteButton(payloadType: String.self) { strings in
                    Task { await model.paste(strings.first) }
                }
                .labelStyle(.iconOnly)
                .buttonBorderShape(.capsule)
                .tint(Color(PgColor.Accent.base))
                .accessibilityIdentifier("import.clipboard")
            }

            if model.emptyClipboard {
                Text(texts.importEmptyClipboard).pgText(PgType.caption).foregroundStyle(Color(PgColor.Status.Warning.text))
            }
        }
        .pgCard()
    }

    private var qr: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            HStack(spacing: PgSpace.l) {
                PgIcon(data: PgIcons.qr, color: Color(PgColor.Accent.base), size: 22)
                VStack(alignment: .leading, spacing: 2) {
                    Text(texts.androidImportQrTitle).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                    Text(texts.iosImportQrText).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
                }
            }

            if DataScannerViewController.isSupported {
                PgTextAction(label: texts.androidImportQrCamera, enabled: !model.qrReading, identifier: "import.qr.camera") {
                    openCamera()
                }
            }

            PhotosPicker(selection: $photo, matching: .images) {
                Text(texts.androidImportQrImage)
                    .pgText(PgType.bodyStrong)
                    .foregroundStyle(Color(model.qrReading ? PgColor.Text.muted : PgColor.Accent.text))
                    .frame(minHeight: PgLayout.touchTarget)
            }
            .disabled(model.qrReading)
            .accessibilityIdentifier("import.qr.image")

            if model.qrReading {
                Text(texts.androidImportQrReading).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
            }

            if let problem = model.qrProblem {
                Text(texts.text(key: problem) ?? "").pgText(PgType.caption).foregroundStyle(Color(PgColor.Status.Warning.text))
                    .accessibilityIdentifier("import.qr.problem")
            }
        }
        .pgCard()
    }

    private var link: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            Overline(text: texts.importLinkTitle)
            PgField(placeholder: texts.importLinkPlaceholder, text: $model.text, style: PgType.mono, multiline: true, identifier: "import.text")
            Text(texts.androidImportHint).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
            PgButton(
                label: texts.importLinkCheck, enabled: !model.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !model.downloading,
                identifier: "import.check"
            ) {
                Task { await model.check(model.text) }
            }

            if model.downloading {
                Text(texts.importDownloading).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
            }

            if let reason = model.preview?.failure {
                let failure = TextKeys.failure(reason)
                let because = texts.text(key: failure.key, [failure.value].compactMap { $0 }) ?? ""
                Text(texts.importDownloadFailed(because)).pgText(PgType.small).foregroundStyle(Color(PgColor.Status.Danger.text))
                    .accessibilityIdentifier("import.failed")
            }
        }
        .pgCard()
    }

    private func preview(_ batch: ImportBatch) -> some View {
        let good = batch.found - batch.failed
        let language = texts.language.rawValue
        let failed = batch.lines.filter { !$0.result.isOk }
        return VStack(alignment: .leading, spacing: PgSpace.s) {
            Overline(text: texts.importPreview + " · " + (texts.text(key: TextKeys.importFormat(batch.format)) ?? batch.format))
            Text(texts.importFound + ": " + String(good)).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
            if batch.failed > 0 {
                Text(String(batch.failed) + " " + (texts.text(key: TextKeys.importErrors(batch.failed, language)) ?? ""))
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Status.Warning.text))
                ForEach(failed.prefix(Self.shownErrors), id: \.number) { line in
                    Text(texts.importLine(line.number, texts.text(key: TextKeys.importError(line.result.error)) ?? ""))
                        .pgText(PgType.caption)
                        .foregroundStyle(Color(PgColor.Text.secondary))
                }

                if failed.count > Self.shownErrors {
                    Text(texts.importMore(failed.count - Self.shownErrors)).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
                }
            }

            if model.preview?.insecure == true {
                // Ключи серверов шли открытым текстом: их могли прочитать по пути.
                Text(texts.importInsecure)
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Status.Caution.text))
                    .padding(PgSpace.m)
                    .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(PgColor.Status.Caution.tint)))
                    .accessibilityIdentifier("import.insecure")
            }

            if model.added == nil, good > 0 {
                Text(texts.importName).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
                PgField(placeholder: texts.importNamePlaceholder, text: $model.name, identifier: "import.name")
            }

            result(good: good, language: language)
        }
        .pgCard()
        .accessibilityIdentifier("import.preview")
    }

    @ViewBuilder
    private func result(good: Int, language: String) -> some View {
        if let added = model.added, added > 0 {
            Text(texts.text(key: TextKeys.imported(added, language), [String(added)]) ?? "")
                .pgText(PgType.bodyStrong)
                .foregroundStyle(Color(PgColor.Status.Success.text))
                .accessibilityIdentifier("import.done")
        } else if model.added != nil || good == 0 {
            Text(good == 0 && model.added == nil ? texts.importEmptyText : texts.importNothing)
                .pgText(PgType.body)
                .foregroundStyle(Color(PgColor.Text.secondary))
                .accessibilityIdentifier("import.done")
        } else {
            PgButton(
                label: texts.text(key: TextKeys.importAdd(good, language), [String(good)]) ?? "", enabled: !model.adding,
                identifier: "import.add"
            ) {
                Task { await model.add() }
            }
        }
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

    /// Доступ к камере спрашивается по нажатию «Сканировать камерой», а не при запуске; отказ — подсказка в карточке QR.
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
