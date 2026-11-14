#!/bin/bash
# Только машина CI с AppKit: настоящий запуск окна с имитацией службы, без TUN и изменения сети.
# Сначала исправленный код; мутанты владения и разделителей обязаны упасть; после возврата исходника — успех.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
[ "$(uname -s)" = Darwin ] || { echo 'Нужна macOS' >&2; exit 1; }
source_file=desktop/src/Client.UI/Services/MacStatusItem.cs
backup=$(mktemp)
cp "$source_file" "$backup"
display_source=desktop/src/Client.UI/Services/MacDisplayRefresh.cs
display_backup=$(mktemp)
cp "$display_source" "$display_backup"
trap 'cp "$backup" "$source_file"; cp "$display_backup" "$display_source"; touch "$source_file" "$display_source"; rm -f "$backup" "$display_backup"' EXIT
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
elif expected == 'separators':
    assert result.returncode != 0, 'Мутант разделителей выжил'
    assert 'Разделители меню macOS:' in result.stderr, 'Мутант упал по другой причине'
    print('Мутант разделителей: обычные пункты вместо линий обнаружены')
else:
    assert result.returncode != 0, 'Мутант без retain выжил'
    assert 'deallocated instance' in result.stderr or 'Значок не пережил' in result.stderr, 'Мутант упал по другой причине'
    print('Мутант без retain: воспроизведено обращение к освобожденному значку')
PY
}
build
check pass

# Настоящий Metal drawable, не Headless: испорченный размер должен восстановиться без пересоздания окна.
check_display() {
  python3 - "$output/bin/Client.UI/release/PrivateGateway.dll" "$1" <<'PY_DISPLAY'
import os, subprocess, sys
result = subprocess.run(['dotnet', sys.argv[1], '--check-display'],
                        env={**os.environ, 'PG_STARTUP_TRACE': '1'},
                        capture_output=True, text=True, timeout=30)
print(result.stdout)
print(result.stderr)
if sys.argv[2] == 'pass':
    assert result.returncode == 0, f'Проверка масштаба упала: {result.returncode}'
    assert 'Масштаб macOS: Metal drawable восстановлен' in result.stdout
else:
    assert result.returncode != 0, 'Мутант без восстановления нативной поверхности выжил'
    assert 'drawable не восстановлен' in result.stderr, 'Мутант упал по другой причине'
    print('Мутант масштаба: устаревший Metal drawable обнаружен')
PY_DISPLAY
}
check_display pass
python3 - "$display_source" <<'PY_DISPLAY_MUTANT'
from pathlib import Path
import sys
path = Path(sys.argv[1])
source = path.read_text()
for old in ('SendVoid(view, backing);', 'SendVoid(layer, display);'):
    assert source.count(old) == 1
    source = source.replace(old, '// Мутант: восстановление нативной поверхности убрано.')
path.write_text(source)
PY_DISPLAY_MUTANT
build
check_display fail
cp "$display_backup" "$display_source"
touch "$display_source"
build
check_display pass
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
python3 - "$source_file" <<'PY_MUTANT'
from pathlib import Path
import sys
path = Path(sys.argv[1])
source = path.read_text()
old = ' && item is not NativeMenuItemSeparator'
assert source.count(old) == 1
path.write_text(source.replace(old, ''))
PY_MUTANT
build
check separators
cp "$backup" "$source_file"
touch "$source_file"
build
check pass
