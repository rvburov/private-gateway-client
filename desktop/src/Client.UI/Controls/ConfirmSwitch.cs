using Avalonia.Controls.Primitives;

namespace PrivateGateway.Client.UI.Controls;

/// <summary>
/// Переключатель с вопросом (блок при обрыве, строгий режим, прокси для локальной сети): по нажатию сам не
/// переключается — нажатие только вызывает команду, а положение приходит из настройки (привязка OneWay). Обычный
/// <see cref="ToggleButton"/> переключился бы до ответа, и отказ его не вернул бы: значение настройки то же, что до
/// нажатия, и привязка не считает его новым.
/// </summary>
public sealed class ConfirmSwitch : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    protected override void Toggle()
    {
    }
}
