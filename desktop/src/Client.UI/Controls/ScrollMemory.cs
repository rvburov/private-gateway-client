using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace PrivateGateway.Client.UI.Controls;

/// <summary>
/// Прокрутка разделов островка («Главная», «Серверы», «Проверка», «Настройки») переживает уход на подстраницу и
/// возврат «Назад»: разметка страницы создается заново при каждом показе, поэтому смещение запоминается по ее модели
/// представления (она живет все время работы окна) и возвращается после раскладки. Подстраницы открываются с начала.
/// </summary>
public static class ScrollMemory
{
    public static readonly AttachedProperty<bool> RememberProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("Remember", typeof(ScrollMemory));

    // Содержимое (списки, итоги проверок) может дорисовываться не за одну раскладку — столько попыток дотянуть
    // смещение до запомненного, потом оно остается каким получилось.
    private const int RestoreAttempts = 10;

    private static readonly ConditionalWeakTable<object, StrongBox<Vector>> Offsets = new();
    private static readonly ConditionalWeakTable<ScrollViewer, Restoring> Pending = new();

    static ScrollMemory() => RememberProperty.Changed.AddClassHandler<ScrollViewer>(OnRememberChanged);

    public static bool GetRemember(ScrollViewer viewer) => viewer.GetValue(RememberProperty);

    public static void SetRemember(ScrollViewer viewer, bool value) => viewer.SetValue(RememberProperty, value);

    private static void OnRememberChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            viewer.AttachedToVisualTree += OnAttached;
            viewer.ScrollChanged += OnScrollChanged;
            viewer.LayoutUpdated += OnLayoutUpdated;
        }
        else
        {
            viewer.AttachedToVisualTree -= OnAttached;
            viewer.ScrollChanged -= OnScrollChanged;
            viewer.LayoutUpdated -= OnLayoutUpdated;
        }
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var viewer = (ScrollViewer)sender!;
        if (viewer.DataContext is { } page && Offsets.TryGetValue(page, out var saved) && saved.Value.Y > 0)
        {
            Pending.AddOrUpdate(viewer, new Restoring(saved.Value));
        }
    }

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var viewer = (ScrollViewer)sender!;
        if (!Pending.TryGetValue(viewer, out var restoring) || viewer.Viewport.Height <= 0)
        {
            return;
        }

        viewer.Offset = restoring.Target;
        if (viewer.Offset.Y >= restoring.Target.Y - 0.5 || ++restoring.Attempts >= RestoreAttempts)
        {
            Pending.Remove(viewer);
        }
    }

    private static void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var viewer = (ScrollViewer)sender!;
        if (viewer.DataContext is { } page)
        {
            Offsets.AddOrUpdate(page, new StrongBox<Vector>(viewer.Offset));
        }
    }

    private sealed class Restoring(Vector target)
    {
        public Vector Target { get; } = target;

        public int Attempts { get; set; }
    }
}
