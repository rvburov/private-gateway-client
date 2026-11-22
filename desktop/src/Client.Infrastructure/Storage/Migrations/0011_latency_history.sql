ALTER TABLE servers ADD COLUMN last_success_latency_ms INTEGER;
ALTER TABLE servers ADD COLUMN last_success_latency_at TEXT;
UPDATE servers SET last_success_latency_ms = latency_ms, last_success_latency_at = latency_checked_at
WHERE latency_ms IS NOT NULL AND latency_failure IS NULL;
