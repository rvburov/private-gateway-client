-- Сохраняем причину и время замера; старый null остается «не проверен».
ALTER TABLE servers ADD COLUMN latency_failure INTEGER;
ALTER TABLE servers ADD COLUMN latency_diagnostic TEXT;
ALTER TABLE servers ADD COLUMN latency_checked_at TEXT;
