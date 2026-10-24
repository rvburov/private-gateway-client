-- Обновление подписок (доска 12). address — адрес без секретной части, только для подписи
-- («https://example.com/sub/••••»); целиком адрес лежит в хранилище секретов ОС. Последняя
-- попытка и ее неудача — чтобы показать ошибку, оставив прошлый список серверов.
ALTER TABLE subscriptions ADD COLUMN address TEXT;
ALTER TABLE subscriptions ADD COLUMN last_attempt_at TEXT;
ALTER TABLE subscriptions ADD COLUMN last_failure TEXT;
ALTER TABLE subscriptions ADD COLUMN last_status INTEGER;

-- Сервер пропал из подписки при обновлении, но выбран — оставлен до выбора другого.
ALTER TABLE servers ADD COLUMN missing_since TEXT;
