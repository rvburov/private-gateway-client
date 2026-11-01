using Avalonia.Controls;

namespace PrivateGateway.Client.UI.Controls;

/// <summary>
/// Сегмент с вопросом («Только прокси», решение 529): по нажатию сам не отмечается — нажатие только вызывает команду, а
/// отметка приходит из настройки (привязка OneWay), как у <see cref="ConfirmSwitch"/>. Обычный <see cref="RadioButton"/>
/// отметился бы до ответа, и отказ его не вернул бы.
/// </summary>
public sealed class ConfirmRadioButton : RadioButton
{
    protected override Type StyleKeyOverride => typeof(RadioButton);

    protected override void Toggle()
    {
    }
}
