# Безопасность / Security

## Как сообщить об уязвимости

Не в Issues: уязвимость в открытом обсуждении увидят раньше, чем выйдет исправление. Сообщите приватно — GitHub →
вкладка **Security** этого репозитория → **Report a vulnerability**. Опишите, что и в какой версии (номер — «Настройки» →
«О программе»), как воспроизвести и что получает атакующий.

Не присылайте ключи серверов, ссылки, адреса подписок и UUID — для разбора они не нужны. Отчет проверки («Проверка» →
«Экспорт отчета») обезличен; приложите его, если он помогает.

Ответ — в течение 3 дней. Утечка трафика мимо туннеля, отказ блока при обрыве, секреты в журнале или на диске открытым
текстом и подмена обновления — первыми: раскатка такого выпуска останавливается сразу (docs/operations/support.md).

## Report a vulnerability

Please do not open a public issue. Use GitHub → **Security** tab of this repository → **Report a vulnerability**, with the
version (Settings → About), steps to reproduce and impact. Never send server keys, share links, subscription URLs or
UUIDs. We answer within 3 days.

## Поддерживаемые версии / Supported versions

Исправления безопасности — в последнем стабильном выпуске и в текущей бете. / Security fixes go to the latest stable
release and the current beta.
