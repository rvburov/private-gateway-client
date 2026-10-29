using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Infrastructure.Secrets;

/// <summary>Хранилище секретов текущей ОС.</summary>
public static class SecretStores
{
    public static ISecretStore ForCurrentOs(SqliteDatabase database)
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiSecretStore(new SecretFile(database));
        }

        if (OperatingSystem.IsMacOS())
        {
            return new KeychainSecretStore();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LibsecretSecretStore();
        }

        throw new PlatformNotSupportedException("Хранилище секретов есть только для Windows, macOS и Linux");
    }
}
