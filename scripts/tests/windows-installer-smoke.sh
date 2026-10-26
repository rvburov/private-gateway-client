#!/bin/bash
#
# Установка и удаление MSI на машине (CI, Windows, Git Bash от администратора): программа в Program Files, ярлык в меню
# «Пуск», запись в «Установленных приложениях», служба поставлена для того, кто ставил, и работает; удаление убирает
# службу, ее папку, файлы программы и ярлык, а чужой файл в папке программы и данные пользователя не трогает. На
# компьютере пользователя не запускать — ставит службу:
#
#   scripts/tests/windows-installer-smoke.sh <версия>
set -euo pipefail
export MSYS_NO_PATHCONV=1

version=${1:?версия выпуска}
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
msi=$(ls artifacts/release/PrivateGateway-"$version"-win-x64.msi)
program="$(cygpath -u "$PROGRAMFILES")/Private Gateway"
shortcut="$(cygpath -u "$PROGRAMDATA")/Microsoft/Windows/Start Menu/Programs/Private Gateway.lnk"
data="$(cygpath -u "$LOCALAPPDATA")/PrivateGateway"

fail() {
  echo "НЕ ТАК: $*" >&2
  exit 1
}

# msiexec: bash ждет конца установки; при ошибке — хвост журнала установщика.
msi_run() {
  local log="artifacts/msi-$1.log"
  msiexec "/$1" "$(cygpath -w "$msi")" /qn /norestart /l*v "$(cygpath -w "$log")" || {
    iconv -f UTF-16LE -t UTF-8 "$log" 2> /dev/null | tail -60 || tail -60 "$log"
    fail "msiexec /$1 закончился с ошибкой"
  }
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
for _ in $(seq 1 50); do
  sc query PrivateGateway | grep -q RUNNING && break
  sleep 0.2
done
sc query PrivateGateway | grep -q RUNNING || fail "служба PrivateGateway не работает: $(sc query PrivateGateway)"
sc qc PrivateGateway | grep -q 'Private Gateway\\Service\\PrivateGateway.Service.exe" --service-name PrivateGateway --owner S-1-5-' \
  || fail "служба запускается не из папки службы или без владельца: $(sc qc PrivateGateway)"

echo mine > "$program/user-notes.txt"
msi_run x
! sc query PrivateGateway > /dev/null 2>&1 || fail "после удаления осталась служба"
[ ! -e "$program/Service" ] || fail "после удаления осталась папка службы"
[ ! -e "$program/PrivateGateway.exe" ] || fail "после удаления осталось окно"
[ ! -e "$shortcut" ] || fail "после удаления остался ярлык"
[ "$(cat "$program/user-notes.txt")" = mine ] || fail "удаление тронуло чужой файл в папке программы"
[ "$(cat "$data/marker")" = keep ] || fail "удаление тронуло данные пользователя"
rm -f "$program/user-notes.txt"
rmdir "$program"
echo "Установщик $msi ставит и удаляет начисто"
