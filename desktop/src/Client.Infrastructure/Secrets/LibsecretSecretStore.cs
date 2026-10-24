using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PrivateGateway.Client.Application.Secrets;

namespace PrivateGateway.Client.Infrastructure.Secrets;

/// <summary>
/// Linux: секрет — запись Secret Service (GNOME Keyring, KWallet) через libsecret, коллекция по
/// умолчанию, атрибут «key». Нет libsecret или службы — хранилище недоступно, и секреты не
/// сохраняются вовсе: открытым текстом в файл они не пишутся.
/// </summary>
/// <remarks>
/// Функции libsecret принимают атрибуты списком переменной длины; здесь он объявлен
/// фиксированным (имя, значение, NULL) — на x86-64 и arm64 Linux это тот же вызов.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class LibsecretSecretStore : ISecretStore
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGlib = "libglib-2.0.so.0";
    private const string Attribute = "key";
    private const string ProbeKey = "privategateway:probe";

    private readonly Lazy<IntPtr> _schema = new(() =>
        secret_schema_new("ru.privategateway.client", 0, Attribute, 0, IntPtr.Zero));

    private readonly Lazy<bool> _available;

    public LibsecretSecretStore() => _available = new Lazy<bool>(Probe);

    public string Name => "Secret Service";

    public bool IsAvailable => _available.Value;

    public void Save(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var stored = secret_password_store_sync(_schema.Value, null, "Private Gateway: " + key, secret, IntPtr.Zero,
            out var error, Attribute, key, IntPtr.Zero);
        Check(error, "сохранить");
        if (stored == 0)
        {
            throw new InvalidOperationException("Secret Service: не удалось сохранить секрет");
        }
    }

    public string? Read(string key)
    {
        var password = secret_password_lookup_sync(_schema.Value, IntPtr.Zero, out var error, Attribute, key, IntPtr.Zero);
        Check(error, "прочитать");
        if (password == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(password);
        }
        finally
        {
            secret_password_free(password);
        }
    }

    public void Delete(string key)
    {
        _ = secret_password_clear_sync(_schema.Value, IntPtr.Zero, out var error, Attribute, key, IntPtr.Zero);
        Check(error, "удалить");
    }

    /// <summary>Есть ли libsecret и отвечает ли служба: пробное чтение несуществующего ключа.</summary>
    private bool Probe()
    {
        try
        {
            var password = secret_password_lookup_sync(_schema.Value, IntPtr.Zero, out var error, Attribute, ProbeKey, IntPtr.Zero);
            if (password != IntPtr.Zero)
            {
                secret_password_free(password);
            }

            if (error != IntPtr.Zero)
            {
                g_error_free(error);
                return false;
            }

            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void Check(IntPtr error, string action)
    {
        if (error == IntPtr.Zero)
        {
            return;
        }

        // GError { GQuark domain; gint code; gchar *message; } — текст после двух 32-битных полей.
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8));
        g_error_free(error);
        throw new InvalidOperationException($"Secret Service: не удалось {action} секрет: {message}");
    }

    [LibraryImport(LibSecret, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr secret_schema_new(string name, int flags, string attribute, int type, IntPtr end);

    [LibraryImport(LibSecret, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int secret_password_store_sync(
        IntPtr schema, string? collection, string label, string password, IntPtr cancellable, out IntPtr error,
        string attribute, string value, IntPtr end);

    [LibraryImport(LibSecret, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr secret_password_lookup_sync(
        IntPtr schema, IntPtr cancellable, out IntPtr error, string attribute, string value, IntPtr end);

    [LibraryImport(LibSecret, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int secret_password_clear_sync(
        IntPtr schema, IntPtr cancellable, out IntPtr error, string attribute, string value, IntPtr end);

    [LibraryImport(LibSecret)]
    private static partial void secret_password_free(IntPtr password);

    [LibraryImport(LibGlib)]
    private static partial void g_error_free(IntPtr error);
}
