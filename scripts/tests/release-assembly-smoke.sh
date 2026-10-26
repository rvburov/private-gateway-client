#!/bin/bash
#
# Проверка scripts/release.cs на собранных здесь «сборках» трех ОС: выпуск собирается с общим SHA256SUMS, манифестом
# обновления (канал, доля раскатки, главное, файлы ОС с суммами) и заметками; бета-версия — канал beta; порченый файл,
# сборка без одной ОС и доля вне 1–100 — ошибка; rollout меняет долю и сумму манифеста. Подпись — на ключе, созданном
# здесь же (keygen): openssl проверяет ее открытым ключом, rollout подписывает заново, ключ не из папки ключей, ключи без
# секрета и rollout подписанного без ключа — ошибка. Запускается в CI (задание «Стиль и формат») и вручную:
#
#   scripts/tests/release-assembly-smoke.sh
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP" "$ROOT/artifacts/release-smoke-key.pem"' EXIT
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

# verify <папка выпуска> <открытый ключ>: подпись update.json.sig сходится с update.json.
verify() {
  base64 -d "$1/update.json.sig" > "$TMP/sig.der" && openssl dgst -sha256 -verify "$2" -signature "$TMP/sig.der" "$1/update.json" > /dev/null 2>&1
}

# Пока ключей нет — выпуск без подписи; папка ключей здесь своя, ключи репозитория проверку не меняют.
export UPDATE_KEYS="$TMP/keys"
unset UPDATE_SIGNING_KEY

builds stable 0.3.0
check "выпуск собирается" "run assemble 0.3.0 '$TMP/stable' '$TMP/stable-out' 20"
check "общий SHA256SUMS сходится" "(cd '$TMP/stable-out' && sha256sum -c --quiet SHA256SUMS)"
check "канал stable и доля 20" "grep -q '\"channel\": \"stable\"' '$TMP/stable-out/update.json' && grep -q '\"rollout\": 20' '$TMP/stable-out/update.json'"
check "в манифесте главное из заметок" "grep -q '\"highlights\": \\[' '$TMP/stable-out/update.json'"
check "без ключей — без подписи" "[ ! -e '$TMP/stable-out/update.json.sig' ]"
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

check "keygen не кладет закрытый ключ в репозиторий" "! run keygen '$ROOT/artifacts/release-smoke-key.pem' && [ ! -e '$ROOT/artifacts/release-smoke-key.pem' ]"
check "keygen: закрытый ключ с правами 600, открытый — в папке ключей" "run keygen '$TMP/key.pem' && [ \"\$(stat -c %a '$TMP/key.pem')\" = 600 ] && ls '$TMP/keys'/*.pem > /dev/null"
check "keygen не перезаписывает ключ" "! run keygen '$TMP/key.pem'"
public=$(ls "$TMP"/keys/*.pem | head -n 1)

check "ключи есть, а секрета нет — ошибка" "! run assemble 0.3.0 '$TMP/stable' '$TMP/nokey-out' && grep -q UPDATE_SIGNING_KEY '$TMP/out.txt'"
export UPDATE_SIGNING_KEY="$(cat "$TMP/key.pem")"
builds signed 0.3.0
check "подписанный выпуск собирается" "run assemble 0.3.0 '$TMP/signed' '$TMP/signed-out' 20 && [ -s '$TMP/signed-out/update.json.sig' ]"
check "подпись проверяется открытым ключом" "verify '$TMP/signed-out' '$public'"
check "подпись — в SHA256SUMS" "grep -q '  update.json.sig\$' '$TMP/signed-out/SHA256SUMS' && (cd '$TMP/signed-out' && sha256sum -c --quiet SHA256SUMS)"
cp -r "$TMP/signed-out" "$TMP/tampered"
sed -i 's/"rollout": 20/"rollout": 100/' "$TMP/tampered/update.json"
check "измененный манифест подпись не проходит" "! verify '$TMP/tampered' '$public'"
check "rollout подписывает заново" "run rollout '$TMP/signed-out' 50 && grep -q '\"rollout\": 50' '$TMP/signed-out/update.json' && verify '$TMP/signed-out' '$public' && (cd '$TMP/signed-out' && sha256sum -c --quiet SHA256SUMS)"
check "rollout подписанного без ключа — ошибка" "! env -u UPDATE_SIGNING_KEY dotnet run scripts/release.cs rollout '$TMP/signed-out' 100 > '$TMP/out.txt' 2>&1 && grep -q '\"rollout\": 50' '$TMP/signed-out/update.json'"
mkdir -p "$TMP/other-keys"
check "ключ не из папки ключей — ошибка" "! UPDATE_KEYS='$TMP/other-keys' UPDATE_SIGNING_KEY=\"\$(openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 2>/dev/null)\" run assemble 0.3.0 '$TMP/signed' '$TMP/foreign-out' && grep -q 'не в репозитории' '$TMP/out.txt'"

if [ "$fails" -gt 0 ]; then
  echo "Проверок не прошло: $fails"
  exit 1
fi
echo "Все проверки сборки выпуска прошли"
