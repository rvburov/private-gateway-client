# Эталон резервной копии (.pgbackup)

`envelope-v1.pgbackup` — файл копии в формате десктопа (`BackupFile`, решение 559): обертка JSON, PBKDF2-SHA256
600 000 повторов, AES-256-GCM, параметры обертки — под защитой тега. Пароль — `correct horse battery staple`
(заведомо тестовый). `envelope-v1.plain.json` — что внутри, байт в байт.

Открывают его все три платформы: десктоп (`desktop/tests/Unit/BackupTests.cs`), Android (`android/core`,
`BackupEnvelopeTest`) и iOS (`ios/Core`, когда там появится копия). Другим паролем файл не открывается.

## Копия телефона между Android и iPhone

`mobile-android.plain.json` и `mobile-ios.plain.json` — содержимое копии телефона (внутри обертки) с одними настройками: так их
пишут Android (значения списков с заглавной: `Custom`, `Every6Hours`) и iPhone (`custom`, `every6Hours`). Тесты `android/core`
(`BackupSettingsTest`) и `ios/Core` (`MobileBackupTests`) читают копию другой платформы и проверяют, что все значения
вернулись (DECISIONS.md, п. 3). Адреса — из диапазонов документации.
