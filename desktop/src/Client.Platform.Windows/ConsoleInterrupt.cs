using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Мягкая остановка консольной программы на Windows — Ctrl+C в ее консоль, как SIGTERM на Linux и macOS (решение 510).
/// sing-box по Ctrl+C закрывает TUN сам: Wintun убирает свой адаптер (WintunCloseAdapter → DIF_REMOVE). Снятый процесс
/// этого не делает, и адаптер с тем же GUID остается в системе: на части машин следующий запуск sing-box ждет 15 с и
/// выходит — «create adapter: Cannot create a file when that file already exists» (у пользователя так начиналось каждое
/// подключение VPN). У ядра своя консоль без окна (CREATE_NO_WINDOW), сигнал в нее шлется так: отцепиться от своей консоли,
/// прицепиться к консоли ядра, на это время не реагировать на Ctrl+C самому, послать, отцепиться и вернуть как было. Консоль
/// у процесса одна, поэтому все — под <see cref="Gate"/>; под ним же служба запускает ядра: процесс, запущенный, пока
/// служба не реагирует на Ctrl+C, унаследовал бы это и не остановился бы мягко.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConsoleInterrupt
{
    private const uint CtrlCEvent = 0;
    private const uint AttachParentProcess = uint.MaxValue;

    /// <summary>Замок консоли процесса: <see cref="Stop"/> и запуск дочерних процессов, которые потом останавливаются через него.</summary>
    public static Lock Gate { get; } = new();

    /// <summary>
    /// Послать Ctrl+C процессу <paramref name="process"/> и ждать его выхода до <paramref name="grace"/>. Вышел — true;
    /// не вышел или сигнал не дошел (у процесса нет консоли, он уже вышел) — false: снимать его — дело вызывающего.
    /// </summary>
    public static bool Stop(Process process, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (Gate)
        {
            if (process.HasExited)
            {
                return true;
            }

            // Своя консоль (запуск из терминала, тесты) — на время отцепиться: прицепиться можно только к одной.
            var hadConsole = FreeConsole();
            var sent = false;
            try
            {
                if (AttachConsole((uint)process.Id))
                {
                    SetConsoleCtrlHandler(IntPtr.Zero, true);
                    sent = GenerateConsoleCtrlEvent(CtrlCEvent, 0);
                }
            }
            finally
            {
                if (sent)
                {
                    // Сигнал дошел до всех в консоли ядра, и до этого процесса тоже — он его пропускает, пока не вернет
                    // реакцию. Возвращать — после выхода ядра: к этому времени сигнал этим процессом точно получен.
                    process.WaitForExit(grace);
                }

                FreeConsole();
                if (hadConsole)
                {
                    AttachConsole(AttachParentProcess);
                }

                SetConsoleCtrlHandler(IntPtr.Zero, false);
            }

            return sent && process.HasExited;
        }
    }

    // Без строк и структур — LibraryImport не нужен.
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
