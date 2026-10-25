using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Startup;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.Infrastructure.Storage;
using PrivateGateway.Client.UI;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Окно целиком, как в программе, на имитации службы: свои часы, буфер обмена и папка данных
/// на каждый тест. Снимки пишутся в artifacts/ui-snapshots — для сверки с макетом.
/// </summary>
public abstract class WindowHarness : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-ui-" + Guid.NewGuid().ToString("N")));
    private ServiceProvider? _services;

    protected FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 24, 15, 42, 0, TimeSpan.Zero));

    protected FakeClipboard Clipboard { get; } = new();

    protected FakeFileSaver Files { get; } = new();

    protected FakeFileOpener OpenFiles { get; } = new();

    protected FakeSecrets Secrets { get; } = new();

    protected FakeDownloader Downloads { get; } = new();

    protected FakeUrlLauncher Urls { get; } = new();

    /// <summary>Автозагрузка ОС в тестах не трогается.</summary>
    protected FakeAutostart Autostart { get; } = new();

    /// <summary>Установка службы: по умолчанию недоступна, как в демо-режиме.</summary>
    protected FakeInstaller Installer { get; } = new();

    /// <summary>Системный прокси пользователя: по умолчанию выключен — тесты не читают настройки машины.</summary>
    protected FakeUserProxy SystemProxy { get; } = new();

    public void Dispose()
    {
        Loc.Instance.SetLanguage("ru");
        _services?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <param name="firstLaunch">true — как при первом запуске: с приветствием (доска 10).</param>
    /// <param name="gateway">Своя служба для окна; null — имитация.</param>
    /// <remarks>Окно — в демо-режиме, на имитации службы, даже если на машине запущена настоящая служба.</remarks>
    protected (MainWindow Window, ShellViewModel Shell) Open(bool firstLaunch = false, Func<IServiceProvider, IGatewayClient>? gateway = null)
    {
        _services = App.BuildServices(_paths, s =>
        {
            s.AddSingleton<TimeProvider>(Time);
            s.AddSingleton<IClipboardService>(Clipboard);
            s.AddSingleton<IFileSaveService>(Files);
            s.AddSingleton<IFileOpenService>(OpenFiles);
            s.AddSingleton<ISecretStore>(Secrets);
            s.AddSingleton<ISubscriptionDownloader>(Downloads);
            s.AddSingleton<IUrlLauncher>(Urls);
            s.AddSingleton<IAutostart>(Autostart);
            s.AddSingleton<IServiceInstaller>(Installer);
            s.AddSingleton<IUserProxySettings>(SystemProxy);
            if (gateway is not null)
            {
                s.AddSingleton<IGatewayClient>(gateway);
            }
        }, demo: true);
        if (!firstLaunch)
        {
            _services.GetRequiredService<SettingsService>().Update(s => s with { OnboardingDone = true });
        }

        var shell = _services.GetRequiredService<ShellViewModel>();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    /// <summary>Служба из контейнера окна — после <see cref="Open"/>.</summary>
    protected T Service<T>()
        where T : notnull => _services!.GetRequiredService<T>();

    protected async Task Connect(ShellViewModel shell)
    {
        var toggle = shell.Protection.ToggleCommand.ExecuteAsync(null);
        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        await toggle;
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
    }

    protected static IEnumerable<Button> Buttons(Window window) => window.GetVisualDescendants().OfType<Button>();

    protected static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    protected static void Press(Window window, string name)
    {
        var button = Buttons(window).Single(b => b.Name == name);
        button.Command!.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    protected static string? Text(Window window, string name) => Find<TextBlock>(window, name).Text;

    // Продолжение задержки имитации идет в пуле потоков, а событие — через очередь окна.
    protected static void WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }

        condition().ShouldBeTrue();
        Dispatcher.UIThread.RunJobs();
    }

    protected static void Snapshot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(Repo.Snapshots, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>
    /// Служба по сценарию: подключение — сразу «Подключение» и итог от <see cref="Outcome"/> (по умолчанию
    /// «Защищено»); режим — из настроек, как у настоящей службы.
    /// </summary>
    protected sealed class ScriptedService(TimeProvider time, Func<AppSettings> settings, ServiceKind kind) : IGatewayClient
    {
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Initial(time.GetUtcNow());

        public ServiceKind Service => kind;

        /// <summary>Что служба сделает из «Подключения».</summary>
        public Func<ConnectionStatus, ConnectionStatus> Outcome { get; init; } = c => c.MoveTo(ConnectionState.Connected, time.GetUtcNow());

        /// <summary>Что покажут проверки; по умолчанию — ничего.</summary>
        public IReadOnlyList<DiagnosticResult> Checks { get; set; } = [];

        public IReadOnlyList<JournalEntry> Journal => [];

        public event EventHandler<ConnectionStatus>? StatusChanged;

        public event EventHandler<JournalEntry>? JournalAdded { add { } remove { } }

        /// <summary>Прислать то же состояние еще раз — как служба после переподключения потока.</summary>
        public void Resend() => StatusChanged?.Invoke(this, Status);

        /// <summary>Прислать измененное состояние — как служба после замера (IP для сайтов).</summary>
        public void Push(Func<ConnectionStatus, ConnectionStatus> change) => Raise(change(Status));

        /// <summary>Выбрано «Продолжить (не рекомендуется)» на «Работает другой VPN»; отключение выбор снимает.</summary>
        public bool OtherVpnAllowed { get; private set; }

        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default)
        {
            var connecting = (Status with { Mode = settings().Mode, ServerId = server.Id }).MoveTo(ConnectionState.Connecting, time.GetUtcNow());
            Raise(connecting);
            Raise(Outcome(connecting));
            return Task.CompletedTask;
        }

        public Task ConnectDespiteOtherVpnAsync(ServerProfile server, CancellationToken cancellationToken = default)
        {
            OtherVpnAllowed = true;
            return ConnectAsync(server, cancellationToken);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            OtherVpnAllowed = false;
            Raise(Status.MoveTo(ConnectionState.Off, time.GetUtcNow()));
            return Task.CompletedTask;
        }

        /// <summary>Как служба в режиме прокси без системного прокси: паузе нечего менять.</summary>
        public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiagnosticReport(Checks, time.GetUtcNow()));

        public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, LatencyResult>>(new Dictionary<string, LatencyResult>());

        private void Raise(ConnectionStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }
    }

    protected sealed class FakeFileSaver : IFileSaveService
    {
        public string? Name { get; private set; }

        public string? Text { get; private set; }

        /// <summary>false — как будто пользователь закрыл окно «Сохранить как».</summary>
        public bool Accept { get; set; } = true;

        public Task<bool> SaveTextAsync(string suggestedName, string text)
        {
            if (Accept)
            {
                Name = suggestedName;
                Text = text;
            }

            return Task.FromResult(Accept);
        }
    }

    /// <summary>Системный прокси, который задает тест.</summary>
    protected sealed class FakeUserProxy : IUserProxySettings
    {
        public UserProxy? Proxy { get; set; }

        public UserProxy? Read() => Proxy;
    }

    /// <summary>Установка службы без запроса прав: отдает заданный итог и выполняет <see cref="OnInstalled"/>.</summary>
    protected sealed class FakeInstaller : IServiceInstaller
    {
        public bool IsAvailable { get; set; }

        public ServiceInstallResult Result { get; set; } = ServiceInstallResult.Installed;

        public Action? OnInstalled { get; set; }

        public int Installs { get; private set; }

        public Task<ServiceInstallResult> InstallAsync(CancellationToken cancellationToken = default)
        {
            Installs++;
            if (Result == ServiceInstallResult.Installed)
            {
                OnInstalled?.Invoke();
            }

            return Task.FromResult(Result);
        }
    }

    protected sealed class FakeAutostart : IAutostart
    {
        public List<bool> Applied { get; } = [];

        public bool IsSupported => true;

        public void Apply(bool enabled) => Applied.Add(enabled);
    }

    protected sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; set; }

        public Task SetTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync() => Task.FromResult(Text);

        /// <summary>Снимок области экрана в буфере; null — в буфере не картинка.</summary>
        public ImagePixels? Image { get; set; }

        public Task<ImagePixels?> GetImageAsync() => Task.FromResult(Image);
    }

    protected sealed class FakeFileOpener : IFileOpenService
    {
        /// <summary>null — как будто пользователь закрыл окно «Открыть».</summary>
        public (string Name, string Text)? File { get; set; }

        public Task<(string Name, string Text)?> OpenTextAsync() => Task.FromResult(File);

        /// <summary>Выбранная картинка; null — окно закрыто.</summary>
        public (string Name, ImagePixels Image)? Image { get; set; }

        public int ImagePickerOpened { get; private set; }

        public Task<(string Name, ImagePixels Image)?> OpenImageAsync()
        {
            ImagePickerOpened++;
            return Task.FromResult(Image);
        }

        /// <summary>Выбранная программа; null — окно закрыто.</summary>
        public (string Path, string Name)? Program { get; set; }

        public Task<(string Path, string Name)?> PickProgramAsync() => Task.FromResult(Program);
    }

    /// <summary>Хранилище секретов в памяти: в облаке и на CI без GNOME Keyring настоящего нет.</summary>
    protected sealed class FakeSecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Available { get; set; } = true;

        public string Name => "Хранилище для тестов";

        public bool IsAvailable => Available;

        public void Save(string key, string secret) =>
            Values[key] = Available ? secret : throw new InvalidOperationException("недоступно");

        public string? Read(string key) => Values.GetValueOrDefault(key);

        public void Delete(string key) => Values.Remove(key);
    }

    /// <summary>Загрузка подписки без сети: адрес → текст (и название из Profile-Title) или ошибка.</summary>
    protected sealed class FakeDownloader : ISubscriptionDownloader
    {
        public Dictionary<Uri, string> Responses { get; } = [];

        public Dictionary<Uri, string> Titles { get; } = [];

        public List<Uri> Requested { get; } = [];

        /// <summary>Ответ, который еще не пришел: тест завершает его сам.</summary>
        public TaskCompletionSource<string>? Pending { get; set; }

        public async Task<SubscriptionContent> DownloadAsync(Uri url, CancellationToken cancellationToken = default)
        {
            Requested.Add(url);
            var text = Pending is { } pending ? await pending.Task.ConfigureAwait(true)
                : Responses.TryGetValue(url, out var known) ? known
                : throw new SubscriptionDownloadException(DownloadFailure.Status, 404);
            return new SubscriptionContent(text, Titles.GetValueOrDefault(url));
        }
    }

    /// <summary>Открытые адреса — вместо браузера.</summary>
    protected sealed class FakeUrlLauncher : IUrlLauncher
    {
        public List<Uri> Opened { get; } = [];

        public Task<bool> OpenAsync(Uri url)
        {
            Opened.Add(url);
            return Task.FromResult(true);
        }
    }
}
