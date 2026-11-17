-- Входные SOCKS5 узлов: адрес и порт не являются секретами. Учетные данные хранит только OS secret store.
CREATE TABLE entry_proxies (
    device_id       TEXT PRIMARY KEY NOT NULL,
    node_id         TEXT NOT NULL,
    host            TEXT NOT NULL,
    port            INTEGER NOT NULL,
    supports_udp    INTEGER NOT NULL DEFAULT 0,
    credentials_key TEXT,
    subscription_key TEXT NOT NULL,
    group_id        TEXT NOT NULL UNIQUE
);

-- Один сохраненный VLESS-профиль использует входной прокси узла. Обычные профили сюда не попадают.
CREATE TABLE entry_proxy_bindings (
    server_id TEXT PRIMARY KEY NOT NULL,
    device_id TEXT NOT NULL REFERENCES entry_proxies(device_id) ON DELETE CASCADE
);

CREATE INDEX entry_proxy_bindings_device ON entry_proxy_bindings(device_id);
