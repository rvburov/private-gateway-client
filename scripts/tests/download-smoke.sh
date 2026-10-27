#!/usr/bin/env bash
# Проверка scripts/download.sh без интернета: свой сервер на 127.0.0.1 обрывает ответы посреди файла, как бывает со
# связью до GitHub (curl 18 «bytes missing»). Загрузка должна докачать файл с места обрыва, а с сервером без докачки —
# начать заново; адрес, которого нет, — отказ с понятной строкой. Нужен python3 (или python на Windows).
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
tmp=$(mktemp -d)
trap 'kill "${server:-0}" 2> /dev/null || true; rm -rf "$tmp"' EXIT
# На Windows python3 бывает заглушкой Магазина — берется тот, что запускается.
python=
for candidate in python3 python; do
  if command -v "$candidate" > /dev/null && "$candidate" -c 'import sys' 2> /dev/null; then
    python=$candidate
    break
  fi
done
[ -n "$python" ] || { echo "download-smoke: нужен python3" >&2; exit 1; }

cat > "$tmp/server.py" << 'PY'
import hashlib, http.server, sys

DATA = bytes((i * 7 + i // 251) % 256 for i in range(1 << 20))
CUT = 300_000
seen = {}

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        ranged = self.headers.get("Range")
        key = (self.path, bool(ranged))
        count = seen[key] = seen.get(key, 0) + 1
        if self.path not in ("/resume", "/norange"):
            self.send_error(404)
            return
        start = 0
        if ranged and self.path == "/resume":
            start = int(ranged.split("=")[1].split("-")[0])
            self.send_response(206)
            self.send_header("Content-Range", f"bytes {start}-{len(DATA) - 1}/{len(DATA)}")
        else:
            self.send_response(200)
        self.send_header("Content-Length", str(len(DATA) - start))
        self.send_header("Connection", "close")
        self.end_headers()
        body = DATA[start:]
        # /resume: целиком файл не отдается никогда — только докачкой, и первая докачка тоже обрывается;
        # /norange: обрывается первый ответ, потом файл целиком, а просьбу о докачке сервер не слышит.
        cut = (not ranged or count == 1) if self.path == "/resume" else count == 1 and not ranged
        try:
            self.wfile.write(body[:CUT] if cut else body)
            self.wfile.flush()
            if cut:
                self.connection.shutdown(2)
        except (BrokenPipeError, ConnectionResetError):
            pass
        self.close_connection = True

if sys.argv[1] == "sha":
    print(hashlib.sha256(DATA).hexdigest())
else:
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    print(server.server_address[1], flush=True)
    server.serve_forever()
PY

expected=$("$python" "$tmp/server.py" sha)
"$python" "$tmp/server.py" serve > "$tmp/port" &
server=$!
for _ in $(seq 50); do [ -s "$tmp/port" ] && break; sleep 0.1; done
port=$(tr -d '[:space:]' < "$tmp/port")

. "$root/scripts/download.sh"
export DOWNLOAD_PAUSE=0
sum() { if command -v sha256sum > /dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
fail() { echo "download-smoke: $*" >&2; exit 1; }

download "http://127.0.0.1:$port/resume" "$tmp/resume.bin" 2> "$tmp/resume.log" || fail "обрыв дважды — не докачано: $(cat "$tmp/resume.log")"
[ "$(sum "$tmp/resume.bin")" = "$expected" ] || fail "докачанный файл не совпал с исходным"
grep -q "попытка 2 из 6" "$tmp/resume.log" || fail "ожидались две оборванные попытки: $(cat "$tmp/resume.log")"

download "http://127.0.0.1:$port/norange" "$tmp/norange.bin" 2> "$tmp/norange.log" || fail "сервер без докачки — не скачано: $(cat "$tmp/norange.log")"
[ "$(sum "$tmp/norange.bin")" = "$expected" ] || fail "файл с сервера без докачки не совпал с исходным"

if download "http://127.0.0.1:$port/missing" "$tmp/missing.bin" 2> "$tmp/missing.log"; then
  fail "адреса нет, а загрузка «удалась»"
fi
grep -q "Не удалось скачать" "$tmp/missing.log" || fail "отказ без понятной строки: $(cat "$tmp/missing.log")"

echo "download-smoke: докачка после обрывов, сервер без докачки и отказ — в порядке"
