-- Устройство и вход в аккаунт сайта: ключ → значение. Таблица не входит в резервную копию:
-- uid устройства создается один раз на установку и на другой компьютер не переносится. Токен — не здесь,
-- а в хранилище секретов ОС. Набор ключей — SqliteAccountStore.
CREATE TABLE IF NOT EXISTS account (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
);
