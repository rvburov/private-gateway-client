namespace PrivateGateway.Client.Contracts;

/// <summary>
/// Итог установки службы для окна. Установка идет от администратора или root в окне ОС, вывод которого окну не виден
/// (запрос UAC на Windows), — причину отказа она пишет в файл в защищенной папке программы: писать туда могут только
/// администраторы и root, читать — все, и подложить туда ссылку на чужой файл обычный пользователь не может.
/// </summary>
public static class SetupResult
{
    public const string FileName = "setup-result.txt";

    /// <summary>Защищенная папка программы: в ней папка установленной службы (ServiceSetup.DefaultFolder).</summary>
    public static string Root => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Private Gateway")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/Private Gateway"
        : "/opt/private-gateway";

    /// <summary>Файл итога для службы в папке по умолчанию.</summary>
    public static string DefaultFile => Path.Combine(Root, FileName);

    /// <summary>Файл итога для службы в папке <paramref name="serviceFolder"/>: рядом с ней.</summary>
    public static string For(string serviceFolder) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(serviceFolder).TrimEnd(Path.DirectorySeparatorChar))!, FileName);
}
