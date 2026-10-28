using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// «Добавить профиль» (доска 3) на эталонах shared/testdata; снимок — для сверки с
/// docs/design/screens/03-import.png.
/// </summary>
public sealed class ImportPageTests : WindowHarness
{
    private static readonly string Subscription =
        File.ReadAllText(Path.Combine(Repo.Root, "shared", "testdata", "subscriptions", "base64", "mixed.txt"));

    [AvaloniaFact]
    public async Task Clipboard_subscription_shows_preview_and_adds_new_servers()
    {
        var (window, shell, page) = OpenImport();
        shell.IsIslandVisible.ShouldBeFalse("экран-шаг без островка");
        Clipboard.Text = Subscription;

        await page.FromClipboardCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Find<Border>(window, "Sheet").IsVisible.ShouldBeFalse();
        Text(window, "FormatText").ShouldBe("base64 · VLESS / Hy2 / Trojan", "порядок — как в фильтре списка серверов");
        Text(window, "FoundCount").ShouldBe("4");
        Text(window, "NewCount").ShouldBe("3");
        Text(window, "DuplicatesCount").ShouldBe("0");
        Text(window, "FailedCount").ShouldBe("1");
        page.FailedLabel.ShouldBe("ошибка");
        page.ErrorLines.ShouldBe(["Строка 4: неверный порт — пропущено."]);
        Find<Button>(window, "AddButton").Content.ShouldBe("Добавить 3 сервера");
        page.ProfileName = "Основная подписка";
        Snapshot(window, "import");

        page.AddCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        shell.Toast.Text.ShouldBe("Добавлено 3 сервера");
        shell.CurrentKey.ShouldBe(Pages.Servers);
        var servers = shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>();
        var group = servers.Groups.ShouldHaveSingleItem();
        group.Title.ShouldBe("Основная подписка · 3");
        group.Rows.Select(r => r.Name).ShouldBe(["Нидерланды 01", "Trojan", "Hy2"], ignoreOrder: true);
        shell.Protection.HasServer.ShouldBeTrue("первый добавленный выбран");
        Secrets.Values.Keys.Count(k => k.StartsWith("server:", StringComparison.Ordinal)).ShouldBe(3);
    }

    [AvaloniaFact]
    public async Task Clash_config_from_clipboard_lists_servers_and_skipped_lines()
    {
        var (window, _, page) = OpenImport();
        Clipboard.Text = File.ReadAllText(Path.Combine(Repo.Root, "shared", "testdata", "subscriptions", "clash", "mixed.txt"));

        await page.FromClipboardCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Text(window, "FormatText").ShouldStartWith("Clash · VLESS / ");
        Text(window, "FoundCount").ShouldBe("18");
        Text(window, "NewCount").ShouldBe("8");
        Text(window, "FailedCount").ShouldBe("10");
        page.ErrorLines[0].ShouldBe("Строка 53: протокол не поддерживается — пропущено.");
        Find<Button>(window, "AddButton").Content.ShouldBe("Добавить 8 серверов");
    }

    [AvaloniaFact]
    public async Task Qr_code_from_a_screen_snapshot_in_the_clipboard_is_imported()
    {
        var (window, _, page) = OpenImport();
        var link = File.ReadAllText(Path.Combine(Repo.Root, "shared", "testdata", "links", "vless", "private-gateway.txt")).Trim();
        Clipboard.Image = QrPicture(link);

        Press(window, "FromQr");
        await WaitForPreviewAsync(page);

        page.IsQr.ShouldBeTrue();
        OpenFiles.ImagePickerOpened.ShouldBe(0, "картинка есть в буфере — окно «Открыть» не нужно");
        Text(window, "FoundCount").ShouldBe("1");
        Text(window, "FormatText").ShouldBe("ссылки · VLESS");
        Find<Button>(window, "AddButton").Content.ShouldBe("Добавить 1 сервер");
    }

    [AvaloniaFact]
    public async Task Without_a_snapshot_the_qr_card_opens_a_picture()
    {
        var (window, _, page) = OpenImport();
        OpenFiles.Image = ("qr.png", QrPicture("trojan://pw-qr@198.51.100.9:443#С картинки"));

        Press(window, "FromQr");
        await WaitForPreviewAsync(page);

        OpenFiles.ImagePickerOpened.ShouldBe(1);
        page.Preview.ShouldNotBeNull().Servers.ShouldHaveSingleItem().Name.ShouldBe("С картинки");
    }

    [AvaloniaFact]
    public async Task Picture_without_a_code_says_so()
    {
        var (window, _, page) = OpenImport();
        Clipboard.Image = new ImagePixels(200, 100, Enumerable.Repeat((byte)0xFF, 200 * 100 * 4).ToArray());

        await page.FromQrCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        page.HasPreview.ShouldBeFalse();
        Text(window, "Message").ShouldBe("QR-код не найден: снимите область с кодом крупнее или выберите другую картинку");
    }

    [AvaloniaFact]
    public async Task Code_read_after_leaving_the_screen_is_dropped()
    {
        var (_, _, page) = OpenImport();

        var reading = page.FromImageAsync(QrPicture("trojan://pw-late@198.51.100.9:443#Поздно"));
        page.BackCommand.Execute(null);
        await reading;
        Dispatcher.UIThread.RunJobs();

        page.HasPreview.ShouldBeFalse("пока распознавали, пользователь ушел — предпросмотр не нужен");
    }

    [AvaloniaFact]
    public void Qr_card_tells_how_to_take_a_snapshot_on_this_os()
    {
        var (_, _, page) = OpenImport();

        page.QrText.ShouldBe(OperatingSystem.IsWindows() ? "Снимок области (Win+Shift+S) или картинка с кодом"
            : OperatingSystem.IsMacOS() ? "Снимок области (Shift+Ctrl+Cmd+4) или картинка с кодом"
            : "Снимок области в буфер обмена или картинка с кодом");
    }

    /// <summary>Распознавание идет в фоне: ждать предпросмотра, прокручивая очередь интерфейса.</summary>
    private static async Task WaitForPreviewAsync(ImportPageViewModel page)
    {
        for (var i = 0; i < 200 && !page.HasPreview && !page.HasMessage; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        page.Message.ShouldBeNull();
    }

    /// <summary>QR-код как снимок: по 4 пикселя на модуль, белое поле в 4 модуля.</summary>
    private static ImagePixels QrPicture(string text)
    {
        var modules = QrCode.Encode(text);
        var size = (modules.GetLength(0) + 8) * 4;
        var pixels = Enumerable.Repeat((byte)0xFF, size * size * 4).ToArray();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var (my, mx) = ((y / 4) - 4, (x / 4) - 4);
                if (my >= 0 && mx >= 0 && my < modules.GetLength(0) && mx < modules.GetLength(1) && modules[my, mx])
                {
                    var at = ((y * size) + x) * 4;
                    pixels[at] = pixels[at + 1] = pixels[at + 2] = 0;
                }
            }
        }

        return new ImagePixels(size, size, pixels);
    }

    [AvaloniaFact]
    public async Task Second_import_of_the_same_text_finds_only_duplicates()
    {
        var (_, _, page) = OpenImport();
        Clipboard.Text = Subscription;
        await page.FromClipboardCommand.ExecuteAsync(null);
        page.AddCommand.Execute(null);

        await page.FromClipboardCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        page.NewText.ShouldBe("0");
        page.DuplicatesText.ShouldBe("3");
        page.AddText.ShouldBe("Новых серверов нет");
        page.CanAdd.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Same_server_with_a_pinned_certificate_updates_the_saved_one_from_the_import_page()
    {
        const string pin = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
        var (_, shell, page) = OpenImport();
        Clipboard.Text = "trojan://pw-pin@198.51.100.31:443?sni=example.com&allowInsecure=1#Self-signed";
        await page.FromClipboardCommand.ExecuteAsync(null);
        page.AddCommand.Execute(null);

        // Тот же сервер с «pcs»: новых нет, но у сохраненного обновится защита (решение 484) — кнопка не гаснет.
        Clipboard.Text = $"trojan://pw-pin@198.51.100.31:443?sni=example.com&pcs={pin}#Self-signed";
        await page.FromClipboardCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        page.NewText.ShouldBe("0");
        page.DuplicatesText.ShouldBe("1");
        page.AddText.ShouldBe("Обновить защиту 1 сервера");
        page.CanAdd.ShouldBeTrue();

        page.AddCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        shell.Toast.Text.ShouldBe("Защита обновлена у 1 сервера");
        var store = Service<IServerStore>();
        store.Definition(store.Servers.ShouldHaveSingleItem().Id)!.Security.PinnedPeerCertSha256.ShouldBe([pin]);
    }

    [AvaloniaFact]
    public async Task Subscription_address_is_downloaded_named_by_host_and_kept_as_a_secret()
    {
        var (window, shell, page) = OpenImport();
        var url = new Uri("https://sub.example.com/api/v1/client/subscribe?token=fake-token-1");
        Downloads.Responses[url] = Subscription;

        page.OpenLinkCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Find<TextBox>(window, "LinkText").Text = "  " + url + "  ";
        await page.CheckLinkCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Downloads.Requested.ShouldBe([url]);
        page.ProfileName.ShouldBe("sub.example.com");
        page.AddCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var servers = shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>();
        servers.Groups.ShouldHaveSingleItem().Title.ShouldBe("sub.example.com · 3");
        Secrets.Values.ShouldContain(kv => kv.Key.StartsWith("subscription:", StringComparison.Ordinal) && kv.Value == url.ToString());
    }

    [AvaloniaFact]
    public async Task Profile_title_of_the_subscription_becomes_the_suggested_name()
    {
        var (window, _, page) = OpenImport();
        var url = new Uri("https://panel.example.com/sub/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        Downloads.Responses[url] = File.ReadAllText(Path.Combine(Repo.Root, "shared", "testdata", "subscriptions", "private-gateway", "base64.txt"));
        Downloads.Titles[url] = "Ноутбук";

        page.OpenLinkCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Find<TextBox>(window, "LinkText").Text = url.ToString();
        await page.CheckLinkCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        page.ProfileName.ShouldBe("Ноутбук", "имя устройства из Profile-Title, а не домен панели");
        Text(window, "FormatText").ShouldBe("base64 · VLESS");
    }

    [AvaloniaFact]
    public async Task Failed_download_is_explained_without_the_address()
    {
        var (window, _, page) = OpenImport();
        page.OpenLinkCommand.Execute(null);
        page.LinkText = "https://sub.example.com/secret-path";

        await page.CheckLinkCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        page.HasPreview.ShouldBeFalse();
        Text(window, "Message").ShouldBe("Подписка не загрузилась: сервер ответил 404 — такой подписки нет, проверьте ссылку");
        Text(window, "Message")!.ShouldNotContain("secret-path");
    }

    [AvaloniaFact]
    public async Task Pasted_links_without_a_name_go_to_my_servers()
    {
        var (_, shell, page) = OpenImport();
        page.OpenLinkCommand.Execute(null);
        page.LinkText = File.ReadAllText(Path.Combine(Repo.Root, "shared", "testdata", "links", "trojan", "ws.txt"));

        await page.CheckLinkCommand.ExecuteAsync(null);
        page.ProfileName.ShouldBeEmpty();
        page.AddCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        shell.Toast.Text.ShouldBe("Добавлен 1 сервер");
        shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>().Groups.ShouldHaveSingleItem().Title.ShouldBe("Мои серверы · 1");
    }

    [AvaloniaFact]
    public async Task File_name_becomes_the_profile_name()
    {
        var (_, _, page) = OpenImport();
        OpenFiles.File = ("Работа.txt", Subscription);

        await page.FromFileCommand.ExecuteAsync(null);

        page.ProfileName.ShouldBe("Работа");
        page.NewText.ShouldBe("3");
        page.IsFile.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task Empty_clipboard_and_text_without_links_are_explained()
    {
        var (window, _, page) = OpenImport();

        await page.FromClipboardCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Text(window, "Message").ShouldBe("В буфере обмена нет текста со ссылками");

        Clipboard.Text = "просто текст";
        await page.FromClipboardCommand.ExecuteAsync(null);
        page.Message.ShouldBeNull();
        page.HasPreview.ShouldBeTrue("строка без ссылки — ошибка разбора, видна в предпросмотре");
        page.ErrorLines.ShouldBe(["Строка 1: не похоже на ссылку — пропущено."]);
        page.CanAdd.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Without_an_os_secret_store_nothing_can_be_added()
    {
        Secrets.Available = false;
        var (window, _, page) = OpenImport();
        Clipboard.Text = Subscription;

        await page.FromClipboardCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Text(window, "StoreWarning").ShouldStartWith("Хранилище для тестов недоступно: серверы с ключами сохранить нельзя.");
        page.CanAdd.ShouldBeFalse();
        Find<Button>(window, "AddButton").IsEffectivelyEnabled.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Subscription_over_plain_http_is_marked_as_insecure()
    {
        var (window, _, page) = OpenImport();
        var url = new Uri("http://sub.example.com/sub/fake-token-2");
        Downloads.Responses[url] = Subscription;
        page.OpenLinkCommand.Execute(null);
        page.LinkText = url.ToString();

        await page.CheckLinkCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Find<TextBlock>(window, "InsecureWarning").IsEffectivelyVisible.ShouldBeTrue();
        page.CanAdd.ShouldBeTrue("предупреждение, а не запрет: у многих панелей подписка только по http");

        Clipboard.Text = Subscription;
        await page.FromClipboardCommand.ExecuteAsync(null);
        page.IsInsecure.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Late_subscription_answer_does_not_replace_a_newer_source()
    {
        var (_, _, page) = OpenImport();
        Downloads.Pending = new TaskCompletionSource<string>();
        page.OpenLinkCommand.Execute(null);
        page.LinkText = "https://sub.example.com/slow";
        var download = page.CheckLinkCommand.ExecuteAsync(null);
        page.IsBusy.ShouldBeTrue();
        page.CanAdd.ShouldBeFalse();

        Clipboard.Text = File.ReadAllText(Path.Combine(Repo.Root, "shared", "testdata", "links", "trojan", "ws.txt"));
        await page.FromClipboardCommand.ExecuteAsync(null);
        Downloads.Pending.SetResult(Subscription);
        await download;

        page.IsBusy.ShouldBeFalse();
        page.FoundText.ShouldBe("1", "остался разбор буфера обмена");
        page.ProfileName.ShouldBeEmpty("имя подписки не подставилось");
        page.CanAdd.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task Answer_that_comes_after_leaving_the_page_is_dropped()
    {
        var (_, shell, page) = OpenImport();
        Downloads.Pending = new TaskCompletionSource<string>();
        page.OpenLinkCommand.Execute(null);
        page.LinkText = "https://sub.example.com/slow";
        var download = page.CheckLinkCommand.ExecuteAsync(null);

        page.BackCommand.Execute(null);
        Downloads.Pending.SetResult(Subscription);
        await download;

        shell.CurrentKey.ShouldBe(Pages.Home);
        page.HasPreview.ShouldBeFalse();
        page.IsBusy.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Many_errors_are_listed_first_three_then_counted()
    {
        var (_, _, page) = OpenImport();
        Clipboard.Text = string.Join('\n', "a", "b", "c", "d", "e");

        await page.FromClipboardCommand.ExecuteAsync(null);

        page.FailedText.ShouldBe("5");
        page.FailedLabel.ShouldBe("ошибок");
        page.ErrorLines.ShouldBe([
            "Строка 1: не похоже на ссылку — пропущено.",
            "Строка 2: не похоже на ссылку — пропущено.",
            "Строка 3: не похоже на ссылку — пропущено.",
            "И еще строк с ошибками: 2.",
        ]);
    }

    [AvaloniaFact]
    public async Task Back_returns_to_the_page_it_came_from_and_forgets_the_preview()
    {
        var (_, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Servers);
        shell.NavigateCommand.Execute(Pages.Add);
        var page = shell.CurrentPage.ShouldBeOfType<ImportPageViewModel>();
        Clipboard.Text = Subscription;
        await page.FromClipboardCommand.ExecuteAsync(null);

        page.BackCommand.Execute(null);

        shell.CurrentKey.ShouldBe(Pages.Servers);
        shell.IsIslandVisible.ShouldBeTrue();
        page.HasPreview.ShouldBeFalse();
        page.IsClipboard.ShouldBeFalse();
    }

    private (MainWindow Window, ShellViewModel Shell, ImportPageViewModel Page) OpenImport()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Add);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<ImportPageViewModel>());
    }
}
