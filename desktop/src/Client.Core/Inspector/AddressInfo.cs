using System.Net;

namespace PrivateGateway.Client.Core.Inspector;

/// <summary>Почему сведений об адресе нет.</summary>
public enum AddressFailure
{
    /// <summary>Адрес частный или служебный (локальная сеть, петля, документация): в интернете о нем ничего нет.</summary>
    Private,

    /// <summary>Источник не ответил: нет сети, сеть закрыта блоком при обрыве, отказ сервиса.</summary>
    Unreachable,

    /// <summary>Источник ответил, но об адресе ничего не знает.</summary>
    NotFound,
}

/// <summary>
/// Что известно об адресе в интернете: автономная система и ее владелец (провайдер или хостинг), объявленная сеть — по
/// данным RIPE NCC (RIPEstat); примерное место — MaxMind GeoLite2 через RIPEstat. Место по IP примерное: у VPN, мобильных
/// сетей и крупных провайдеров город может быть другим, а страна — страной регистрации сети.
/// </summary>
/// <param name="Address">Адрес, о котором спрашивали.</param>
/// <param name="Asn">Номер автономной системы; неизвестен — null.</param>
/// <param name="Holder">Владелец автономной системы.</param>
/// <param name="Prefix">Объявленная сеть, в которую входит адрес.</param>
/// <param name="Country">Страна — код ISO 3166 (NL, DE).</param>
/// <param name="City">Город, если база его знает.</param>
/// <param name="Failure">Почему сведений нет; есть — null.</param>
public sealed record AddressInfo(IPAddress Address, long? Asn, string? Holder, string? Prefix, string? Country, string? City, AddressFailure? Failure)
{
    public static AddressInfo Failed(IPAddress address, AddressFailure failure) => new(address, null, null, null, null, null, failure);
}
