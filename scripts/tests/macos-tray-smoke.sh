#!/bin/bash
# Только машина CI с AppKit: настоящий запуск окна с имитацией службы, без TUN и изменения сети.
# Сначала исправленный код; затем мутант без retain обязан упасть, после возврата исходника — снова успех.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
[ "$(uname -s)" = Darwin ] || { echo 'Нужна macOS' >&2; exit 1; }
source_file=desktop/src/Client.UI/Services/MacStatusItem.cs
backup=$(mktemp)
cp "$source_file" "$backup"
trap 'cp "$backup" "$source_file"; touch "$source_file"; rm -f "$backup"' EXIT
output="$PWD/artifacts/tray-smoke"
mkdir -p "$output"
build() {
  dotnet build desktop/src/Client.UI -c Release -p:PgPrepareDevelopmentService=false \
    -p:ArtifactsPath="$output" --nologo > "$output/build.log" 2>&1 || { cat "$output/build.log"; exit 1; }
}
check() {
  python3 - "$output/bin/Client.UI/release/PrivateGateway.dll" "$1" <<'PY'
import os, subprocess, sys
expected = sys.argv[2]
try:
    result = subprocess.run(['dotnet', sys.argv[1], '--check-tray'],
                            env={**os.environ, 'NSZombieEnabled': 'YES', 'PG_STARTUP_TRACE': '1'},
                            capture_output=True, text=True, timeout=30)
except subprocess.TimeoutExpired as error:
    stdout = error.stdout.decode(errors='replace') if isinstance(error.stdout, bytes) else error.stdout or ''
    stderr = error.stderr.decode(errors='replace') if isinstance(error.stderr, bytes) else error.stderr or ''
    print(stdout)
    print(stderr)
    # NSZombie может оставить AppKit работающим после ошибки вместо немедленного SIGABRT.
    # Один тайм-аут не считается воспроизведением: обязательна диагностика освобожденного объекта.
    assert expected == 'fail' and 'deallocated instance' in stderr, 'Трей завис без ожидаемой ошибки владения'
    print('Мутант без retain: зависание после обращения к освобожденному значку')
    sys.exit(0)
print(result.stdout)
print(result.stderr)
if expected == 'pass':
    assert result.returncode == 0, f'Трей упал: {result.returncode}'
    assert 'Трей macOS:' in result.stdout, 'Проверка не дошла до конца'
else:
    assert result.returncode != 0, 'Мутант без retain выжил'
    assert 'deallocated instance' in result.stderr or 'Значок не пережил' in result.stderr, 'Мутант упал по другой причине'
    print('Мутант без retain: воспроизведено обращение к освобожденному значку')
PY
}
build
check pass
python3 - "$source_file" <<'PY'
from pathlib import Path
import sys
path = Path(sys.argv[1])
source = path.read_text()
old = '_item = Send(SendDouble(_bar, Selector("statusItemWithLength:"), -2), Selector("retain"));'
assert source.count(old) == 1
path.write_text(source.replace(old, '_item = SendDouble(_bar, Selector("statusItemWithLength:"), -2);'))
PY
build
check fail
cp "$backup" "$source_file"
touch "$source_file"
build
check pass
