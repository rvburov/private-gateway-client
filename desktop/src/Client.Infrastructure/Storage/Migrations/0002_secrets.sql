-- Секреты, зашифрованные DPAPI текущего пользователя Windows (DpapiSecretStore). На macOS и
-- Linux таблица пустая: там секреты в Связке ключей и Secret Service.
CREATE TABLE secrets (
    key   TEXT PRIMARY KEY NOT NULL,
    value BLOB NOT NULL
);
