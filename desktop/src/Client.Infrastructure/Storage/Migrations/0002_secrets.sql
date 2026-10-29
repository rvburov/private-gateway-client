-- Секреты, зашифрованные DPAPI текущего пользователя Windows (DpapiSecretStore). На macOS и
-- Linux таблица пустая: там секреты в Связке ключей и Secret Service. С 2026-10-29 секреты Windows — в secrets.db
-- (Secrets/SecretFile): таблицу заполняли прежние версии, при создании файла ее строки переносятся туда.
CREATE TABLE secrets (
    key   TEXT PRIMARY KEY NOT NULL,
    value BLOB NOT NULL
);
