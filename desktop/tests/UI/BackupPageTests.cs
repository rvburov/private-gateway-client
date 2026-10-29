using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Infrastructure.Backup;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>«Резервная копия» (доска 14); снимок — для сверки с docs/design/screens/14-backup.png.</summary>
public sealed class BackupPageTests : WindowHarness
{
    private const string Password = "correct horse battery";

    [AvaloniaFact]
    public void Server_copies_warn_about_subscription_access_even_without_server_keys()
    {
        var (window, _, page) = OpenBackup();

        page.IncludeKeys.ShouldBeFalse();
        Find<Border>(window, "SubscriptionWarning").IsVisible.ShouldBeTrue();
        page.IncludeServers = false;
        Dispatcher.UIThread.RunJobs();
        Find<Border>(window, "SubscriptionWarning").IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Defaults_match_the_board()
    {
        var (window, shell, page) = OpenBackup();

        shell.IsSettings.ShouldBeTrue();
        (page.IncludeServers, page.IncludeSettings, page.IncludeKeys).ShouldBe((true, true, false));
        Find<Border>(window, "KeysWarning").IsVisible.ShouldBeFalse();
        Text(window, "AutoCopyText").ShouldBe("Появится перед первым восстановлением");
        Find<Button>(window, "RestoreAutoCopyButton").IsVisible.ShouldBeFalse();
        Find<TextBox>(window, "SavePassword").PasswordChar.ShouldBe('•');
        page.Password = "секретный пароль";
        Snapshot(window, "backup");
    }

    [AvaloniaFact]
    public void Keys_go_only_with_servers_and_come_with_a_warning()
    {
        var (window, _, page) = OpenBackup();

        page.IncludeKeys = true;
        Dispatcher.UIThread.RunJobs();
        Find<Border>(window, "KeysWarning").IsVisible.ShouldBeTrue();

        page.IncludeServers = false;
        Dispatcher.UIThread.RunJobs();
        page.IncludeKeys.ShouldBeFalse("ключам без серверов не к чему относиться");
        Find<CheckBox>(window, "PartKeys").IsEnabled.ShouldBeFalse();
        Find<Border>(window, "KeysWarning").IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Saving_checks_the_choice_and_the_password_then_writes_the_file()
    {
        var (window, shell, page) = OpenBackup();

        page.IncludeServers = false;
        page.IncludeSettings = false;
        await page.SaveCommand.ExecuteAsync(null);
        page.SaveError.ShouldBe("Выберите, что сохранить");

        page.IncludeSettings = true;
        page.Password = "1234567";
        await page.SaveCommand.ExecuteAsync(null);
        page.SaveError.ShouldBe("Пароль — не короче 8 знаков");
        Files.Text.ShouldBeNull();

        Files.Accept = false;
        page.Password = Password;
        await page.SaveCommand.ExecuteAsync(null);
        page.Password.ShouldBe(Password, "окно сохранения закрыто — пароль остается");

        Files.Accept = true;
        await page.SaveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        page.SaveError.ShouldBeNull();
        Find<TextBlock>(window, "SaveError").IsVisible.ShouldBeFalse();
        Files.Name.ShouldBe("private-gateway-2026-10-24.pgbackup");
        BackupService.IsBackup(Files.Text!).ShouldBeTrue();
        var content = BackupService.Read(Files.Text!, Password);
        (content.HasServers, content.HasSettings, content.HasKeys).ShouldBe((false, true, false));
        page.Password.ShouldBeEmpty("пароль в памяти экрана не задерживается");
        shell.Toast.Text.ShouldBe("Копия сохранена");
    }

    [AvaloniaFact]
    public async Task Restore_asks_the_password_shows_the_contents_and_replaces_after_an_autocopy()
    {
        var (window, shell, page) = OpenBackup();
        var store = Service<IServerStore>();
        store.Add(Parse("trojan://pw-copy@198.51.100.1:443#Из копии"), null);
        Service<SettingsService>().Update(s => s with { KillSwitch = false });
        var file = Service<BackupService>().Create(new BackupParts(Servers: true, Settings: true, Keys: true), Password);
        store.RemoveSubscription(null);
        store.Add(Parse("trojan://pw-now@198.51.100.2:443#Сейчас"), null);
        Service<SettingsService>().Update(s => s with { KillSwitch = true });

        OpenFiles.File = ("copy.pgbackup", file);
        await page.ChooseFileCommand.ExecuteAsync(null);
        var sheet = shell.Overlay.Sheet.ShouldBeOfType<RestoreBackupViewModel>();
        (sheet.FileName, sheet.IsLocked).ShouldBe(("copy.pgbackup", true));

        sheet.Password = "wrong password";
        await sheet.OpenCommand.ExecuteAsync(null);
        sheet.Error.ShouldBe("Пароль не подходит или файл поврежден");
        store.Servers.ShouldHaveSingleItem().Name.ShouldBe("Сейчас", "до «Восстановить» ничего не меняется");

        sheet.Password = Password;
        await sheet.OpenCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        sheet.IsOpened.ShouldBeTrue();
        sheet.Password.ShouldBeEmpty();
        sheet.Summary.ShouldBe("Копия от 24 октября 2026, 15:42: серверы и подписки (1, с ключами), настройки.");
        Snapshot(window, "backup-restore-sheet");

        sheet.RestoreCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        shell.Overlay.HasSheet.ShouldBeFalse();
        store.Servers.ShouldHaveSingleItem().Name.ShouldBe("Из копии");
        Service<SettingsService>().Current.KillSwitch.ShouldBeFalse();
        shell.Toast.Text.ShouldBe("Восстановлено: 1 сервер, настройки");
        Text(window, "AutoCopyText").ShouldBe("сегодня в 15:42 · с ключами");
        Find<Button>(window, "RestoreAutoCopyButton").IsVisible.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task Autocopy_brings_back_the_state_before_the_restore()
    {
        var (_, shell, page) = OpenBackup();
        var store = Service<IServerStore>();
        store.Add(Parse("trojan://pw-copy@198.51.100.1:443#Из копии"), null);
        var file = Service<BackupService>().Create(new BackupParts(Servers: true, Settings: false, Keys: true), Password);
        store.RemoveSubscription(null);
        store.Add(Parse("trojan://pw-now@198.51.100.2:443#Сейчас"), null);
        page.Apply(BackupService.Read(file, Password));
        store.Servers.ShouldHaveSingleItem().Name.ShouldBe("Из копии");

        var undo = page.RestoreAutoCopyCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var dialog = shell.Overlay.Dialog.ShouldNotBeNull();
        (dialog.Title, dialog.PrimaryText).ShouldBe(("Вернуть автокопию?", "Отмена"));
        shell.Overlay.DismissCommand.Execute(null);
        await undo;
        store.Servers.ShouldHaveSingleItem().Name.ShouldBe("Из копии", "Escape — ничего не меняется");

        undo = page.RestoreAutoCopyCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog!.SecondaryCommand.Execute(null);
        await undo;

        store.Servers.ShouldHaveSingleItem().Name.ShouldBe("Сейчас");
        store.Definition(store.Servers[0].Id)!.Credentials.Password.ShouldBe("pw-now");
        shell.Toast.Text.ShouldBe("Восстановлено: 1 сервер, настройки");
    }

    [AvaloniaFact]
    public void Restored_language_switches_the_window_and_skipped_servers_are_named()
    {
        var (_, shell, page) = OpenBackup();
        Service<IServerStore>().Add(Parse("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B"), null);
        Service<SettingsService>().Update(s => s with { Language = "en" });
        var file = Service<BackupService>().Create(new BackupParts(Servers: true, Settings: true, Keys: false), Password);
        Service<IServerStore>().RemoveSubscription(null);
        Service<SettingsService>().Update(s => s with { Language = "ru" });

        page.Apply(BackupService.Read(file, Password));

        Loc.Instance.Language.ShouldBe("en");
        shell.Toast.Text.ShouldBe("Restored: 0 servers, settings · 2 servers without keys skipped");
    }

    [AvaloniaFact]
    public async Task Other_files_are_turned_away_before_asking_for_a_password()
    {
        var (_, shell, page) = OpenBackup();

        OpenFiles.File = ("links.txt", "trojan://pw@198.51.100.1:443#A");
        await page.ChooseFileCommand.ExecuteAsync(null);

        shell.Overlay.HasSheet.ShouldBeFalse();
        shell.Toast.Text.ShouldBe("Это не файл резервной копии");
    }

    private static List<Core.Servers.ServerDefinition> Parse(params string[] links) =>
        [.. links.Select(l => LinkParser.Parse(l).Server.ShouldNotBeNull(l))];

    private (MainWindow Window, ShellViewModel Shell, BackupPageViewModel Page) OpenBackup()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Backup);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<BackupPageViewModel>());
    }
}
