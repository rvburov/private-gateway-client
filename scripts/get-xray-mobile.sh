#!/usr/bin/env bash
# Xray-core той же версии, что внутри libXray телефонов, — программа xray для проверки конфигов Android и iOS
# (ReferenceConfigTest, ReferenceConfigTests): `xray run -test` читает конфиг тем же загрузчиком и собирает те же
# обработчики, что `TestXray` libXray, без запуска. Коммит libXray — из android/libxray.version (у iOS тот же), версия
# Xray-core и всех модулей Go — из go.mod и go.sum этого коммита: go сверяет суммы, недостающая сумма — ошибка
# (-mod=readonly). Версия Go — из go.mod libXray (GOTOOLCHAIN=auto скачает ее с proxy.golang.org).
# Нужны git и go. Результат — artifacts/cores/xray-mobile/ (в git не хранится). Только для тестов.
# Запуск: scripts/get-xray-mobile.sh
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
commit=$(tr -d '[:space:]' < "$root/android/libxray.version")
dest="$root/artifacts/cores/xray-mobile"

command -v go > /dev/null || { echo "Нет go — Xray libXray не собрать" >&2; exit 1; }
exe=$(go env GOEXE)
# Версия Go — та, что в go.mod libXray, даже если setup-go поставил GOTOOLCHAIN=local.
export GOTOOLCHAIN=auto

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

git init -q "$tmp/src"
git -C "$tmp/src" fetch -q --depth 1 https://github.com/XTLS/libXray "$commit"
git -C "$tmp/src" -c advice.detachedHead=false checkout -q FETCH_HEAD

core=$(awk '$1 == "github.com/xtls/xray-core" { print $2 }' "$tmp/src/go.mod")
[ -n "$core" ] || { echo "В go.mod libXray нет github.com/xtls/xray-core" >&2; exit 1; }

rm -rf "$dest"
mkdir -p "$dest"
# Флаги — как у сборки libXray (scripts/get-libxray.sh): -checklinkname=0 нужен Xray-core.
(
  cd "$tmp/src"
  GOFLAGS=-mod=readonly CGO_ENABLED=0 go build -o "$dest/xray$exe" -trimpath -buildvcs=false \
    -ldflags="-checklinkname=0 -s -w -buildid=" github.com/xtls/xray-core/main
)
echo "Xray-core $core (libXray $commit) → $dest (только для тестов)"
"$dest/xray$exe" version | head -1
