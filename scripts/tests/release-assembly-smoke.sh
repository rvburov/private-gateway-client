#!/bin/bash
#
# Проверка scripts/release.cs на собранных здесь «сборках» трех ОС и APK: выпуск собирается с общим SHA256SUMS и
# манифестом обновления (канал, доля раскатки — без доли 100 %, страница скачивания сайта в notes, главное из секции
# версии в CHANGELOG.md, файлы ОС с суммами; APK — android-apk, если он есть, без него выпуск тоже собирается);
# бета-версия — канал beta; версия без секции в истории, порченый файл (и APK), сборка без одной ОС, notes не https и
# доля вне 1–100 — ошибка; rollout меняет долю и сумму манифеста, 0 — останавливает раскатку.
# Подпись — на ключе, созданном здесь же (keygen): openssl проверяет ее открытым ключом, rollout подписывает заново, ключ
# не из папки ключей, ключи без секрета и rollout подписанного без ключа — ошибка. Запускается в CI (задание «Стиль и формат») и вручную:
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

# builds <папка> <версия> [без ОС]: сборки трех ОС, как их кладет publish.yml, и APK, как его кладет release.yml (папка
# release-Android, ее SHA256SUMS — .apk.sha256 задания «Android (APK)»).
builds() {
  local dir="$TMP/$1" v="$2" deb=${2//-/\~}
  mkdir -p "$dir/release-Linux" "$dir/release-Windows" "$dir/release-macOS" "$dir/release-Android"
  echo deb > "$dir/release-Linux/private-gateway_${deb}_amd64.deb"
  echo rpm > "$dir/release-Linux/private-gateway-${deb}-1.x86_64.rpm"
  echo tar > "$dir/release-Linux/PrivateGateway-$v-linux-x64.tar.gz"
  echo msi > "$dir/release-Windows/PrivateGateway-$v-win-x64.msi"
  echo mac > "$dir/release-macOS/PrivateGateway-$v-osx-arm64.tar.gz"
  echo dmg > "$dir/release-macOS/PrivateGateway-$v-osx-arm64.dmg"
  echo apk > "$dir/release-Android/PrivateGateway-$v-android.apk"
  [ -z "${3:-}" ] || rm -rf "$dir/release-$3"
  for os in "$dir"/release-*; do (cd "$os" && sha256sum * > SHA256SUMS); done
}

# asset <update.json> <вид> <файл>: в манифесте у вида — имя, SHA-256 и размер этого файла.
asset() {
  python3 -c 'import hashlib,json,os,sys; a=json.load(open(sys.argv[1]))["assets"].get(sys.argv[2]); f=sys.argv[3]; sys.exit(a != {"file": os.path.basename(f), "sha256": hashlib.sha256(open(f,"rb").read()).hexdigest(), "size": os.path.getsize(f)})' "$@"
}

# notes <update.json> <адрес>: ссылка «Скачать на сайте» в манифесте — ровно этот адрес.
notes() {
  python3 -c 'import json,sys; sys.exit(json.load(open(sys.argv[1]))["notes"] != sys.argv[2])' "$@"
}

run() { dotnet run scripts/release.cs "$@" > "$TMP/out.txt" 2>&1; }

# verify <папка выпуска> <открытый ключ>: подпись update.json.sig сходится с update.json.
verify() {
  base64 -d "$1/update.json.sig" > "$TMP/sig.der" && openssl dgst -sha256 -verify "$2" -signature "$TMP/sig.der" "$1/update.json" > /dev/null 2>&1
}

# Пока ключей нет — выпуск без подписи; папка ключей здесь своя, ключи репозитория проверку не меняют.
export UPDATE_KEYS="$TMP/keys"

# История версий: главное манифеста берется только из секции своей версии; «Не выпущено» и чужие версии не в счет.
# Текст страницы выпуска из той же секции собирает scripts/release-notes.sh — его проверяет scripts/tests/release-test.sh.
cat > "$TMP/CHANGELOG.md" <<'CHANGES'
# Изменения

## [Не выпущено]

### Добавлено

- Общий список до выпуска, в 0.3.0 не попадает.

## [0.4.0-beta.1]

### Добавлено

- Бета.

## [0.3.0] — 1 декабря

### Добавлено — 1 декабря

- Окно подтверждения перед раздельным режимом. Подробнее
  в инструкции.

### Исправлено — 2 декабря

- Строка реального IP не пропадает.

## [0.2.0]

### Добавлено — 1 ноября

- Старое, в 0.3.0 не попадает.
CHANGES
export RELEASE_CHANGELOG="$TMP/CHANGELOG.md"
# Страница в notes — по умолчанию сайт; своя страница из окружения разработчика проверку не меняет.
unset UPDATE_SIGNING_KEY UPDATE_NOTES_URL

builds stable 0.3.0
check "выпуск собирается" "run assemble 0.3.0 '$TMP/stable' '$TMP/stable-out' 20"
check "общий SHA256SUMS сходится" "(cd '$TMP/stable-out' && sha256sum -c --quiet SHA256SUMS)"
check "канал stable и доля 20" "grep -q '\"channel\": \"stable\"' '$TMP/stable-out/update.json' && grep -q '\"rollout\": 20' '$TMP/stable-out/update.json'"
check "в манифесте главное из заметок" "grep -q '\"highlights\": \\[' '$TMP/stable-out/update.json'"
check "без ключей — без подписи" "[ ! -e '$TMP/stable-out/update.json.sig' ]"
check "в манифесте MSI, deb и образ macOS" "grep -q win-x64-msi '$TMP/stable-out/update.json' && grep -q linux-x64-deb '$TMP/stable-out/update.json' && grep -q osx-arm64-dmg '$TMP/stable-out/update.json'"
check "notes — страница скачивания сайта, а не GitHub" "notes '$TMP/stable-out/update.json' https://private-gateway.duckdns.org/downloads"
check "APK в манифесте — android-apk: имя, SHA-256 и размер" "asset '$TMP/stable-out/update.json' android-apk '$TMP/stable/release-Android/PrivateGateway-0.3.0-android.apk'"
check "rpm в манифесте — linux-x64-rpm" "asset '$TMP/stable-out/update.json' linux-x64-rpm '$TMP/stable/release-Linux/private-gateway-0.3.0-1.x86_64.rpm'"
check "APK — в общем SHA256SUMS" "grep -q '  PrivateGateway-0.3.0-android.apk\$' '$TMP/stable-out/SHA256SUMS'"
check "без доли — 100 %: выпуск всем сразу" "run assemble 0.3.0 '$TMP/stable' '$TMP/full-out' && grep -q '\"rollout\": 100' '$TMP/full-out/update.json'"
builds noapk 0.3.0 Android
check "без APK выпуск собирается, android-apk в манифесте нет" "run assemble 0.3.0 '$TMP/noapk' '$TMP/noapk-out' 20 && ! grep -q android-apk '$TMP/noapk-out/update.json' && grep -q win-x64-msi '$TMP/noapk-out/update.json'"
check "UPDATE_NOTES_URL — своя страница в notes" "UPDATE_NOTES_URL=https://example.org/downloads run assemble 0.3.0 '$TMP/stable' '$TMP/notes-out' 20 && notes '$TMP/notes-out/update.json' https://example.org/downloads"
check "UPDATE_NOTES_URL не https — ошибка: такой манифест программа не примет" "! UPDATE_NOTES_URL=http://example.org/downloads run assemble 0.3.0 '$TMP/stable' '$TMP/http-out' 20 && grep -q 'UPDATE_NOTES_URL' '$TMP/out.txt' && [ ! -e '$TMP/http-out/update.json' ]"
check "главное манифеста — из той же секции, первое предложение" "python3 -c 'import json,sys; h=json.load(open(sys.argv[1]))[\"highlights\"]; sys.exit(h != [\"Окно подтверждения перед раздельным режимом.\", \"Строка реального IP не пропадает.\"])' '$TMP/stable-out/update.json'"
builds nosection 0.2.9
check "версия без секции в истории — ошибка, коммиты не подставляются" "! run assemble 0.2.9 '$TMP/nosection' '$TMP/nosection-out' && grep -q 'нет секции' '$TMP/out.txt'"
check "rollout меняет долю и сумму" "run rollout '$TMP/stable-out' 50 && grep -q '\"rollout\": 50' '$TMP/stable-out/update.json' && (cd '$TMP/stable-out' && sha256sum -c --quiet SHA256SUMS)"
check "доля 0 останавливает раскатку" "run rollout '$TMP/stable-out' 0 && grep -q '\"rollout\": 0' '$TMP/stable-out/update.json' && (cd '$TMP/stable-out' && sha256sum -c --quiet SHA256SUMS)"
check "доля 101 — ошибка" "! run rollout '$TMP/stable-out' 101"

builds beta 0.4.0-beta.1
check "бета — канал beta" "run assemble 0.4.0-beta.1 '$TMP/beta' '$TMP/beta-out' && grep -q '\"channel\": \"beta\"' '$TMP/beta-out/update.json'"
check "бета — APK своей версии в манифесте" "asset '$TMP/beta-out/update.json' android-apk '$TMP/beta/release-Android/PrivateGateway-0.4.0-beta.1-android.apk'"

builds broken 0.3.0
echo corrupt >> "$TMP/broken/release-macOS/PrivateGateway-0.3.0-osx-arm64.tar.gz"
check "порченый файл — ошибка" "! run assemble 0.3.0 '$TMP/broken' '$TMP/broken-out' && grep -q 'сумма не та' '$TMP/out.txt'"

builds broken-apk 0.3.0
echo corrupt >> "$TMP/broken-apk/release-Android/PrivateGateway-0.3.0-android.apk"
check "APK, испорченный между заданиями, — ошибка" "! run assemble 0.3.0 '$TMP/broken-apk' '$TMP/broken-apk-out' && grep -q 'android.apk: файла нет или сумма не та' '$TMP/out.txt'"

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
