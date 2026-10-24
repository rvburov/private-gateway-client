using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Core;
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

    protected (MainWindow Window, ShellViewModel Shell) Open()
    {
        _services = App.BuildServices(_paths, s =>
        {
            s.AddSingleton<TimeProvider>(Time);
            s.AddSingleton<IClipboardService>(Clipboard);
            s.AddSingleton<IFileSaveService>(Files);
        });
        var shell = _services.GetRequiredService<ShellViewModel>();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

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

    protected sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }
}
