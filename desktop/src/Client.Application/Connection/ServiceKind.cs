namespace PrivateGateway.Client.Application.Connection;

/// <summary>Какая служба отвечает окну.</summary>
public enum ServiceKind
{
    /// <summary>Службы нет: подключиться нельзя — экран «Служба не запущена» (доска 16).</summary>
    None,

    /// <summary>Установленная служба с правами администратора: режимы VPN и прокси; работает и без окна.</summary>
    Installed,

    /// <summary>Служба пользователя без прав: только режим прокси; останавливается вместе с окном.</summary>
    User,

    /// <summary>Имитация: демо-режим (<c>--demo</c>) и тесты окна.</summary>
    Simulated,
}
