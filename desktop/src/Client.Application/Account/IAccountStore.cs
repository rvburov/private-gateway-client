using PrivateGateway.Client.Core.Account;

namespace PrivateGateway.Client.Application.Account;

/// <summary>
/// Это устройство для сайта. uid — случайный, создается один раз на установку и не попадает в резервную копию:
/// перенос копии на другой компьютер не делает его тем же устройством.
/// </summary>
public interface IDeviceIdentity
{
    /// <summary>uid установки; при первом чтении создается и сохраняется.</summary>
    /// <exception cref="InvalidOperationException">Базу программы не прочитать и не записать.</exception>
    string Uid { get; }

    /// <summary>Имя устройства для списка на сайте: имя компьютера.</summary>
    string Name { get; }

    /// <summary>Версия программы, как в «О программе».</summary>
    string AppVersion { get; }
}

/// <summary>Сведения входа без токена (<see cref="SavedAccount"/>): в базе программы, вне резервной копии.</summary>
public interface IAccountStore
{
    /// <returns>null — входа нет.</returns>
    SavedAccount? Load();

    void Save(SavedAccount account);

    /// <summary>Забыть вход; uid устройства остается.</summary>
    void Clear();
}

/// <summary>Нужен ли вход: в демо-режиме (<c>--demo</c>) программа работает без него.</summary>
public sealed record AccountOptions(bool Required = true);
