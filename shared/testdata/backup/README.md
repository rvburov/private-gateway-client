# Эталон резервной копии (.pgbackup)

`envelope-v1.pgbackup` — файл копии в формате десктопа (`BackupFile`, решение 559): обертка JSON, PBKDF2-SHA256
600 000 повторов, AES-256-GCM, параметры обертки — под защитой тега. Пароль — `correct horse battery staple`
(заведомо тестовый). `envelope-v1.plain.json` — что внутри, байт в байт.

Открывают его все три платформы: десктоп (`desktop/tests/Unit/BackupTests.cs`), Android (`android/core`,
`BackupEnvelopeTest`) и iOS (`ios/Core`, когда там появится копия). Другим паролем файл не открывается.
