-- Группы серверов (профили, подписки): название и время обновления. Адрес подписки — в нем токен
-- доступа — хранится только в хранилище секретов ОС под ключом subscription:<id>.
CREATE TABLE subscriptions (
    id         TEXT PRIMARY KEY NOT NULL,
    name       TEXT NOT NULL,
    position   INTEGER NOT NULL,
    updated_at TEXT,
    created_at TEXT NOT NULL
);

-- Серверы: описание без секретов (JSON) и то, что меняет пользователь. Секретная часть — UUID,
-- пароли, ключи — только в хранилище секретов ОС под ключом server:<id>.
CREATE TABLE servers (
    id              TEXT PRIMARY KEY NOT NULL,
    subscription_id TEXT REFERENCES subscriptions(id) ON DELETE CASCADE,
    position        INTEGER NOT NULL,
    definition      TEXT NOT NULL,
    custom_name     TEXT,
    latency_ms      INTEGER,
    is_favorite     INTEGER NOT NULL DEFAULT 0,
    last_used_at    TEXT,
    created_at      TEXT NOT NULL
);
