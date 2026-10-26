#!/bin/bash
#
# Проверка scripts/release.cs на собранных здесь «сборках» трех ОС: выпуск собирается с общим SHA256SUMS, манифестом
# обновления (канал, доля раскатки, файлы ОС с суммами) и заметками; бета-версия — канал beta; порченый файл, сборка
# без одной ОС и доля вне 1–100 — ошибка; rollout меняет долю и сумму манифеста. Запускается в CI (задание «Стиль и
# формат») и вручную:
#
#   scripts/tests/release-assembly-smoke.sh
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
cd "$ROOT"
fails=0

check() {
  if eval "$2"; then echo "ок: $1"; else echo "НЕ ТАК: $1"; fails=$((fails + 1)); fi
}

# builds <папка> <версия> [без ОС]: сборки трех ОС, как их кладет publish.yml.
builds() {
  local dir="$TMP/$1" v="$2" deb=${2//-/\~}
  mkdir -p "$dir/release-Linux" "$dir/release-Windows" "$dir/release-macOS"
  echo deb > "$dir/release-Linux/private-gateway_${deb}_amd64.deb"
  echo rpm > "$dir/release-Linux/private-gateway-${deb}-1.x86_64.rpm"
  echo tar > "$dir/release-Linux/PrivateGateway-$v-linux-x64.tar.gz"
  echo msi > "$dir/release-Windows/PrivateGateway-$v-win-x64.msi"
  echo mac > "$dir/release-macOS/PrivateGateway-$v-osx-arm64.tar.gz"
  [ -z "${3:-}" ] || rm -rf "$dir/release-$3"
  for os in "$dir"/release-*; do (cd "$os" && sha256sum * > SHA256SUMS); done
}

run() { dotnet run scripts/release.cs "$@" > "$TMP/out.txt" 2>&1; }

builds stable 0.3.0
check "выпуск собирается" "run assemble 0.3.0 '$TMP/stable' '$TMP/stable-out' 20"
check "общий SHA256SUMS сходится" "(cd '$TMP/stable-out' && sha256sum -c --quiet SHA256SUMS)"
check "канал stable и доля 20" "grep -q '\"channel\": \"stable\"' '$TMP/stable-out/update.json' && grep -q '\"rollout\": 20' '$TMP/stable-out/update.json'"
check "в манифесте MSI, deb и macOS" "grep -q win-x64-msi '$TMP/stable-out/update.json' && grep -q linux-x64-deb '$TMP/stable-out/update.json' && grep -q osx-arm64-tar '$TMP/stable-out/update.json'"
check "заметки рядом, а не в файлах выпуска" "[ -f '$TMP/notes.md' ] && [ ! -e '$TMP/stable-out/notes.md' ]"
check "rollout меняет долю и сумму" "run rollout '$TMP/stable-out' 50 && grep -q '\"rollout\": 50' '$TMP/stable-out/update.json' && (cd '$TMP/stable-out' && sha256sum -c --quiet SHA256SUMS)"
check "доля 0 — ошибка" "! run rollout '$TMP/stable-out' 0"

builds beta 0.4.0-beta.1
check "бета — канал beta" "run assemble 0.4.0-beta.1 '$TMP/beta' '$TMP/beta-out' && grep -q '\"channel\": \"beta\"' '$TMP/beta-out/update.json'"

builds broken 0.3.0
echo corrupt >> "$TMP/broken/release-macOS/PrivateGateway-0.3.0-osx-arm64.tar.gz"
check "порченый файл — ошибка" "! run assemble 0.3.0 '$TMP/broken' '$TMP/broken-out' && grep -q 'сумма не та' '$TMP/out.txt'"

builds partial 0.3.0 Windows
check "без сборки Windows — ошибка" "! run assemble 0.3.0 '$TMP/partial' '$TMP/partial-out' && grep -q 'win-x64-msi' '$TMP/out.txt'"

if [ "$fails" -gt 0 ]; then
  echo "Проверок не прошло: $fails"
  exit 1
fi
echo "Все проверки сборки выпуска прошли"
