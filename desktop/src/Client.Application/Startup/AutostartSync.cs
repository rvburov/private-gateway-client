using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Application.Startup;

/// <summary>
/// Держит автозагрузку ОС в согласии с настройкой «Запуск вместе с ОС», но только по явному решению:
/// когда пользователь переключил настройку и один раз — после первого запуска (там настройка
/// включена по умолчанию). Если пользователь сам убрал программу из автозагрузки средствами ОС,
/// программа ее туда не возвращает.
/// </summary>
public sealed class AutostartSync(SettingsService settings, IAutostart autostart)
{
    private AppSettings? _last;

    public void Start()
    {
        _last = settings.Current;
        settings.Changed += (_, next) => OnChanged(next);
    }

    private void OnChanged(AppSettings next)
    {
        var previous = _last ?? next;
        _last = next;
        var switched = previous.StartWithSystem != next.StartWithSystem;
        var firstLaunchDone = !previous.OnboardingDone && next.OnboardingDone;
        if ((switched || firstLaunchDone) && autostart.IsSupported)
        {
            autostart.Apply(next.StartWithSystem);
        }
    }
}
