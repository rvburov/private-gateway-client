#!/bin/bash
#
# Установка, обновление, откат и удаление MSI на машине (CI, Windows, Git Bash от администратора): программа в Program
# Files, ярлык в меню «Пуск», запись в «Установленных приложениях», служба поставлена для того, кто ставил, без файлов
# окна, с состоянием в своей папке, и работает. Обновление (тот же выпуск в MSI с версией выше) заменяет службу и
# сохраняет ее состояние; неудачное обновление (неверный владелец) откатывается к прежней версии с работающей службой;
# новая копия службы, которая не запускается, сменяется прежней; MSI прежней версии ставится поверх новой (откат на
# прежнюю версию из «О программе») со службой и ее состоянием. Удаление убирает службу, ее папки, файлы программы и
# ярлык, а чужой файл в папке программы и данные пользователя не трогает. На компьютере пользователя не запускать —
# ставит службу:
#
#   scripts/tests/windows-installer-smoke.sh <версия>
set -euo pipefail
export MSYS_NO_PATHCONV=1
# Python GitHub Runner использует системную cp1252 при перенаправленном stdout; отчеты и JSON проекта — UTF-8.
export PYTHONUTF8=1

version=${1:?версия выпуска}
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
msi=$(ls artifacts/release/PrivateGateway-"$version"-win-x64.msi)
# Переменные Windows в Git Bash — то в верхнем регистре, то как в Windows.
program="$(cygpath -u "${PROGRAMFILES:-${ProgramFiles:-C:\Program Files}}")/Private Gateway"
shortcut="$(cygpath -u "${PROGRAMDATA:-${ProgramData:-C:\ProgramData}}")/Microsoft/Windows/Start Menu/Programs/Private Gateway.lnk"
data="$(cygpath -u "${LOCALAPPDATA:-${LocalAppData:-$USERPROFILE\AppData\Local}}")/PrivateGateway"

fail() {
  echo "НЕ ТАК: $*" >&2
  exit 1
}

# msiexec: bash ждет конца установки; при ошибке — хвост журнала установщика. msi_run <i|x> [файл] [свойства…]
msi_run() {
  local action=$1 file=${2:-$msi} log="artifacts/msi-$1-$RANDOM.log"
  shift $(( $# < 2 ? $# : 2 ))
  msiexec "/$action" "$(cygpath -w "$file")" /qn /norestart /l*v "$(cygpath -w "$log")" "$@" || {
    if iconv -f UTF-16LE -t UTF-8 "$log" > "$log.txt" 2> /dev/null; then
      # Конец журнала — обычно только свойства MSI. Причина сбоя custom-action находится раньше.
      grep -nE -B 8 -A 12 'Return value 3|WixQuietExec|RollbackUpgradeService|[Ee]rror [0-9]+|Ошибка' "$log.txt" | tail -100 || true
      tail -20 "$log.txt"
    else
      tail -60 "$log"
    fi
    fail "msiexec /$action $file закончился с ошибкой"
  }
}

running() {
  for _ in $(seq 1 100); do
    sc query PrivateGateway | grep -q RUNNING && return 0
    sleep 0.2
  done
  return 1
}

# Версия в «Установленных приложениях»: поиск по имени дает ключ записи, из него — DisplayVersion.
installed_version() {
  local key
  key=$(reg query 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' /s /f 'Private Gateway' /d | tr -d '\r' | grep -m 1 '^HKEY')
  reg query "$key" /v DisplayVersion | tr -d '\r' | sed -n 's/.*DisplayVersion *REG_SZ *//p'
}

mkdir -p "$data"
echo keep > "$data/marker"

msi_run i
for file in PrivateGateway.exe PrivateGateway.Service.exe cores/xray/xray.exe cores/sing-box/sing-box.exe THIRD-PARTY-NOTICES.txt \
  sbom.cdx.json Service/PrivateGateway.Service.exe; do
  [ -f "$program/$file" ] || fail "после установки нет $program/$file"
done
[ -f "$shortcut" ] || fail "нет ярлыка в меню «Пуск»"
reg query 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' /s /f 'Private Gateway' /d | grep -q DisplayName \
  || fail "нет записи в «Установленных приложениях»"
[ "$("$program/Service/PrivateGateway.Service.exe" --version)" = "Private Gateway Service $version (IPC v1)" ] || fail "служба — не $version"
running || fail "служба PrivateGateway не работает: $(sc query PrivateGateway)"
sc qc PrivateGateway | grep -q 'Private Gateway\\Service\\PrivateGateway.Service.exe" --service-name PrivateGateway --owner S-1-5-' \
  || fail "служба запускается не из папки службы или без владельца: $(sc qc PrivateGateway)"
sc qc PrivateGateway | grep -q -- '--state "C:\\Program Files\\Private Gateway\\ServiceState"' || fail "у службы нет папки состояния: $(sc qc PrivateGateway)"
[ ! -e "$program/Service/PrivateGateway.exe" ] && [ ! -e "$program/Service/libSkiaSharp.dll" ] || fail "служба возит с собой окно"
for _ in $(seq 1 50); do [ -f "$program/ServiceState/journal.jsonl" ] && break; sleep 0.2; done
[ -f "$program/ServiceState/journal.jsonl" ] || fail "служба не пишет журнал в папку состояния"
# Номер раскатки — состояние службы, которое должно пережить обновление (служба заменила бы только неверный номер).
echo 42 > "$program/ServiceState/update-bucket"
owner=$(sc qc PrivateGateway | grep -o -- '--owner S-1-5-[0-9-]*')
msi_version=$(installed_version)
[ "$msi_version" = "${version%%-*}" ] || fail "в «Установленных приложениях» версия «$msi_version»"

# Обновление: та же сборка в MSI с версией выше — служба заменяется и снова работает, ее состояние на месте.
IFS=. read -r major minor patch <<< "${version%%-*}"
newer=$(scripts/package-windows.sh "$version" "$major.$minor.$((patch + 1))" | tail -n 1)
# Позднее RemoveExistingProducts опирается на стабильные component GUID. Читаем настоящие Component rows из обоих MSI
# через декомпилятор: одинаковые файлы и целевые пути не должны стать другими компонентами из-за новой версии пакета.
wix msi decompile "$msi" -sui -sct -sras -o artifacts/msi-base-components.wxs
wix msi decompile "$newer" -sui -sct -sras -o artifacts/msi-next-components.wxs
python3 - artifacts/msi-base-components.wxs artifacts/msi-next-components.wxs <<'PY'
import re, sys
import xml.etree.ElementTree as ET

def components(path):
    result = {}
    for item in ET.parse(path).getroot().iter():
        if item.tag.rsplit("}", 1)[-1] != "Component":
            continue
        guid = item.get("Guid", "").strip("{}").lower()
        if not re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", guid):
            raise SystemExit(f"{path}: у {item.get('Id')} нет конкретного ComponentId из MSI")
        result[item.attrib["Id"]] = guid
    if not result:
        raise SystemExit(f"{path}: таблица Component пуста")
    return result

base, newer = map(components, sys.argv[1:])
changed = sorted(key for key in base.keys() | newer.keys() if base.get(key) != newer.get(key))
if changed:
    raise SystemExit("Компоненты одинаковой раскладки поменяли GUID: " + ", ".join(changed[:10]))
print(f"Стабильные ComponentId подтверждены в двух MSI: {len(base)} компонентов")
PY
msi_run i "$newer"
[ "$(installed_version)" = "$major.$minor.$((patch + 1))" ] || fail "после обновления версия $(installed_version)"
running || fail "после обновления служба не работает: $(sc query PrivateGateway)"
[ "$(cat "$program/ServiceState/update-bucket")" = 42 ] || fail "обновление потеряло состояние службы"
[ ! -e "$program/Service.old" ] && [ ! -e "$program/Service.new" ] || fail "после обновления остались папки Service.old/.new"

# Неудачное обновление: служба не ставится (владелец — не SID) — MSI откатывается, прежняя версия и служба на месте.
newest=$(scripts/package-windows.sh "$version" "$major.$minor.$((patch + 2))" | tail -n 1)
if msiexec /i "$(cygpath -w "$newest")" /qn /norestart OWNER=1000 /l*v "$(cygpath -w artifacts/msi-failed.log)"; then
  fail "обновление с неверным владельцем прошло"
fi
[ "$(installed_version)" = "$major.$minor.$((patch + 1))" ] || fail "после отката версия $(installed_version), а не прежняя"
running || fail "после отката служба не работает: $(sc query PrivateGateway)"
sc qc PrivateGateway | grep -q -- "$owner" || fail "после отката у службы другой владелец"
[ -f "$program/PrivateGateway.exe" ] || fail "после отката нет окна"

# MSI падает после того, как новая служба уже запущена. Ее конфиг отличается безопасной меткой: deferred-action пишет
# метку в журнал и падает, rollback возвращает старые файлы программы, затем из них — прежнюю службу без сброса состояния.
runtime="artifacts/publish/PrivateGateway-$version-win-x64/PrivateGateway.Service.runtimeconfig.json"
cp "$runtime" artifacts/service-runtime-before-smoke.json
old_runtime_sum=$(sha256sum "$program/Service/PrivateGateway.Service.runtimeconfig.json" | cut -d' ' -f1)
python3 - "$runtime" <<'PY'
import json, sys
path = sys.argv[1]
with open(path) as file:
    data = json.load(file)
data["auditSmokeMarker"] = "SMOKE_NEW_SERVICE"
with open(path, "w") as file:
    json.dump(data, file, indent=2)
PY
late_failure=$(PGC_MSI_SMOKE_FAIL_AFTER_SERVICE=1 scripts/package-windows.sh "$version" "$major.$minor.$((patch + 3))" | tail -n 1)
cp artifacts/service-runtime-before-smoke.json "$runtime"
if msiexec /i "$(cygpath -w "$late_failure")" /qn /norestart /l*v "$(cygpath -w artifacts/msi-late-failure.log)"; then
  fail "MSI с ошибкой после запуска новой службы прошел"
fi
iconv -f UTF-16LE -t UTF-8 artifacts/msi-late-failure.log > artifacts/msi-late-failure.txt
grep -q 'WixQuietExec:.*SMOKE_NEW_SERVICE' artifacts/msi-late-failure.txt || fail "не доказано, что новая копия службы встала до ошибки MSI"
[ "$(installed_version)" = "$major.$minor.$((patch + 1))" ] || fail "поздний откат MSI потерял прежнюю версию"
running || fail "после позднего отката MSI служба не работает"
[ "$(sha256sum "$program/Service/PrivateGateway.Service.runtimeconfig.json" | cut -d' ' -f1)" = "$old_runtime_sum" ] \
  || fail "после позднего отката MSI осталась новая копия службы"
[ "$(cat "$program/ServiceState/update-bucket")" = 42 ] || fail "поздний откат потерял состояние службы"
sc qc PrivateGateway | grep -q -- "$owner" || fail "поздний откат изменил владельца службы"

# Новая копия службы не запускается (нет нужной библиотеки) — установка возвращает прежнюю и отвечает ошибкой.
broken=$(mktemp -d)
for item in "$program"/*; do
  case "${item##*/}" in
    Service | ServiceState) ;; # копия службы и ее состояние (service.lock занят работающей службой) — не из сборки
    *) cp -r "$item" "$broken/" ;;
  esac
done
rm -f "$broken/Grpc.AspNetCore.Server.dll"
if "$broken/PrivateGateway.Service.exe" --install ${owner}; then
  fail "установка службы, которая не запускается, прошла"
fi
rm -rf "$broken"
running || fail "после неудачной замены служба не работает"
[ -f "$program/Service/Grpc.AspNetCore.Server.dll" ] || fail "после неудачной замены на месте не прежняя копия"
[ ! -e "$program/Service.old" ] && [ ! -e "$program/Service.new" ] || fail "после отката остались папки Service.old/.new"

# Откат на прежнюю версию: MSI старше ставится поверх новой (понижение версии разрешено) — служба того же владельца
# работает, ее состояние на месте.
msi_run i "$msi"
[ "$(installed_version)" = "${version%%-*}" ] || fail "после отката на прежнюю версию стоит $(installed_version)"
running || fail "после отката на прежнюю версию служба не работает: $(sc query PrivateGateway)"
sc qc PrivateGateway | grep -q -- "$owner" || fail "после отката на прежнюю версию у службы другой владелец"
[ "$(cat "$program/ServiceState/update-bucket")" = 42 ] || fail "откат на прежнюю версию потерял состояние службы"
[ -f "$program/PrivateGateway.exe" ] && [ -f "$shortcut" ] || fail "после отката на прежнюю версию нет окна или ярлыка"

echo mine > "$program/user-notes.txt"
# Отказ CLI удаления не должен позволять RemoveFiles убрать окно и оставить работающую службу без управления.
mv "$program/PrivateGateway.Service.exe" "$program/PrivateGateway.Service.exe.hidden"
if msiexec /x "$(cygpath -w "$msi")" /qn /norestart /l*v "$(cygpath -w artifacts/msi-uninstall-failed.log)"; then
  mv "$program/PrivateGateway.Service.exe.hidden" "$program/PrivateGateway.Service.exe"
  fail "MSI удален, хотя служба не могла быть удалена"
fi
mv "$program/PrivateGateway.Service.exe.hidden" "$program/PrivateGateway.Service.exe"
[ -f "$program/PrivateGateway.exe" ] && [ -f "$shortcut" ] || fail "отказ удаления убрал окно или ярлык"
[ "$(installed_version)" = "${version%%-*}" ] || fail "отказ удаления потерял регистрацию MSI"
running || fail "отказ удаления остановил службу"
msi_run x "$msi"
! sc query PrivateGateway > /dev/null 2>&1 || fail "после удаления осталась служба"
[ ! -e "$program/Service" ] || fail "после удаления осталась папка службы"
[ ! -e "$program/ServiceState" ] || fail "после удаления осталось состояние службы"
[ ! -e "$program/PrivateGateway.exe" ] || fail "после удаления осталось окно"
[ ! -e "$shortcut" ] || fail "после удаления остался ярлык"
[ "$(cat "$program/user-notes.txt")" = mine ] || fail "удаление тронуло чужой файл в папке программы"
[ "$(cat "$data/marker")" = keep ] || fail "удаление тронуло данные пользователя"
rm -f "$program/user-notes.txt"
rmdir "$program"
echo "Установщик $msi ставит, обновляет, откатывается и удаляет начисто (была версия $msi_version)"
