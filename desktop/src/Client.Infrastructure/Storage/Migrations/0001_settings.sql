-- Настройки программы: ключ → значение (текст). Набор ключей — SqliteSettingsStore.
CREATE TABLE settings (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
);
