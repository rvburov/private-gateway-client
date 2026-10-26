namespace PrivateGateway.Client.Application.Notifications;

/// <summary>
/// Уведомления ОС: их рисует сама ОС, программа задает заголовок, текст и до двух кнопок (доска 22). Режим «Не беспокоить»
/// ОС соблюдается: важное — со звуком, но не «критическое», которое его пробивает.
/// </summary>
public interface ISystemNotifier
{
    /// <summary>Умеет ли ОС показывать уведомления этой программе (macOS — только из пакета .app).</summary>
    bool IsSupported { get; }

    /// <summary>Показать уведомление; прежнее с той же меткой заменяется. Отказ ОС — молча: значок в трее все равно скажет.</summary>
    Task ShowAsync(string tag, NoticeText text, bool important, CancellationToken cancellationToken = default);

    /// <summary>Убрать уведомление с этой меткой, если оно еще висит: беда прошла.</summary>
    Task CloseAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>Пользователь нажал уведомление или его кнопку. Может прийти из любого потока.</summary>
    event EventHandler<NoticeReply>? Replied;
}

/// <summary>Окно программы глазами уведомлений.</summary>
public interface INoticeWindow
{
    /// <summary>Окно открыто и активно — вместо уведомления ОС всплывашка в окне.</summary>
    bool IsActive { get; }

    /// <summary>Окно на экране, а не спрятано в трей: «Подключено» и «Отключено» тогда не нужны.</summary>
    bool IsVisible { get; }

    /// <summary>Всплывашка внутри окна.</summary>
    void ShowInWindow(Notice notice);
}

/// <summary>Тексты уведомлений на языке пользователя.</summary>
public interface INoticeTexts
{
    NoticeText Text(Notice notice);
}

/// <summary>ОС, которая уведомлений не показывает: macOS вне пакета .app, Linux без службы уведомлений.</summary>
public sealed class NoSystemNotifier : ISystemNotifier
{
    public bool IsSupported => false;

    public event EventHandler<NoticeReply>? Replied
    {
        add { }
        remove { }
    }

    public Task ShowAsync(string tag, NoticeText text, bool important, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CloseAsync(string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
