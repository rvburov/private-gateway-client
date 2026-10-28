#!/usr/bin/env bash
# Сервер для проверки туннеля Android на эмуляторе (android/app/src/androidTest, пункт 9.4): Xray с входом VLESS без
# TLS на порту 10086 (UUID — заведомо ненастоящий, из примеров) и HTTP на 127.0.0.1:18080 с ответом «pg-tunnel-ok».
# Соединения на порт 18081 любого адреса Xray отправляет на этот HTTP: проверочный 1.1.1.1:18081 напрямую не отвечает,
# а через туннель — отвечает (адрес публичный: диапазоны документации клиент сам закрывает как особые сети, решение 446).
# Остальное — в интернет машины CI, как у настоящего сервера: DNS и проба связи службы (cp.cloudflare.com) идут через
# туннель по-настоящему. Эмулятор видит машину как 10.0.2.2. Нужен Xray: scripts/get-xray.sh. Выходу freedom разрешено все
# (finalRules): Xray-core 26.x по умолчанию не пускает трафик входа VLESS на частные адреса, а HTTP проверки — на 127.0.0.1.
# Запуск: scripts/android-test-server.sh — сервер остается работать в фоне до конца задания CI.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
xray="$root/artifacts/cores/xray/xray"
[ -x "$xray" ] || { echo "Нет Xray — сначала scripts/get-xray.sh" >&2; exit 1; }

dir="$root/artifacts/android-test-server"
mkdir -p "$dir/www"
echo "pg-tunnel-ok" > "$dir/www/index.html"
cat > "$dir/xray.json" <<'JSON'
{
  "log": { "loglevel": "warning" },
  "inbounds": [
    {
      "listen": "0.0.0.0",
      "port": 10086,
      "protocol": "vless",
      "settings": { "clients": [{ "id": "11111111-2222-3333-4444-555555555555" }], "decryption": "none" }
    }
  ],
  "outbounds": [
    { "tag": "direct", "protocol": "freedom", "settings": { "finalRules": [{ "action": "allow" }] } },
    { "tag": "site", "protocol": "freedom", "settings": { "redirect": "127.0.0.1:18080", "finalRules": [{ "action": "allow" }] } }
  ],
  "routing": { "rules": [{ "type": "field", "port": "18081", "outboundTag": "site" }] }
}
JSON

(cd "$dir/www" && nohup python3 -m http.server 18080 --bind 127.0.0.1 > "$dir/http.log" 2>&1 &)
nohup "$xray" run -c "$dir/xray.json" > "$dir/xray.log" 2>&1 &

for _ in $(seq 1 20); do
  if curl -sf --max-time 2 http://127.0.0.1:18080/ > /dev/null && (exec 3<>/dev/tcp/127.0.0.1/10086) 2> /dev/null; then
    echo "Сервер проверки туннеля готов: VLESS 0.0.0.0:10086 → HTTP 127.0.0.1:18080"
    exit 0
  fi
  sleep 0.5
done

echo "Сервер проверки туннеля не поднялся" >&2
cat "$dir/xray.log" "$dir/http.log" >&2 || true
exit 1
