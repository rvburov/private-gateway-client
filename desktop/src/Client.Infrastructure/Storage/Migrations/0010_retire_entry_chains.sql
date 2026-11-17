-- Снятая экспериментальная цепочка не должна превратиться в прямое подключение.
-- Очистка каталога и очередь удаления секретов фиксируются одной транзакцией миграции.
CREATE TABLE retired_secret_keys (key TEXT PRIMARY KEY NOT NULL);
CREATE TEMP TABLE retired_groups AS
    SELECT group_id AS id FROM entry_proxies
    UNION SELECT id FROM subscriptions WHERE address LIKE '%/sub/entry/%';
CREATE TEMP TABLE retired_servers AS
    SELECT server_id AS id FROM entry_proxy_bindings
    UNION SELECT id FROM servers WHERE subscription_id IN (SELECT id FROM retired_groups);

INSERT OR IGNORE INTO retired_secret_keys SELECT credentials_key FROM entry_proxies WHERE credentials_key IS NOT NULL;
INSERT OR IGNORE INTO retired_secret_keys SELECT subscription_key FROM entry_proxies;
INSERT OR IGNORE INTO retired_secret_keys SELECT 'subscription:' || id FROM retired_groups;
INSERT OR IGNORE INTO retired_secret_keys SELECT 'server:' || id FROM retired_servers;

DELETE FROM settings WHERE key = 'selected_server' AND value IN (SELECT id FROM retired_servers);
DELETE FROM servers WHERE id IN (SELECT id FROM retired_servers);
DELETE FROM subscriptions WHERE id IN (SELECT id FROM retired_groups);
DROP TABLE entry_proxy_bindings;
DROP TABLE entry_proxies;
DROP TABLE retired_servers;
DROP TABLE retired_groups;
