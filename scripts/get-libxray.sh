#!/usr/bin/env bash
# Ядро Xray для Android: libXray (XTLS, MIT) — Xray-core в .aar через gomobile. Коммит libXray — из
# android/libxray.version, версия Xray-core и всех модулей Go — из go.mod и go.sum этого коммита (go сверяет суммы
# при загрузке). Нужны git, go и Android NDK: ANDROID_NDK_HOME или ANDROID_NDK_LATEST_HOME (GitHub Actions).
# Результат — android/app/libs/libXray.aar (в git не хранится). Запуск: scripts/get-libxray.sh
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
commit=$(tr -d '[:space:]' < "$root/android/libxray.version")
dest="$root/android/app/libs"

command -v go > /dev/null || { echo "Нет go — libXray не собрать" >&2; exit 1; }
export ANDROID_NDK_HOME="${ANDROID_NDK_HOME:-${ANDROID_NDK_LATEST_HOME:-}}"
[ -n "$ANDROID_NDK_HOME" ] && [ -d "$ANDROID_NDK_HOME" ] || { echo "Нет Android NDK: задайте ANDROID_NDK_HOME" >&2; exit 1; }

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

git init -q "$tmp/src"
git -C "$tmp/src" fetch -q --depth 1 https://github.com/XTLS/libXray "$commit"
git -C "$tmp/src" -c advice.detachedHead=false checkout -q FETCH_HEAD

# gomobile и gobind — той же версии, что в go.mod libXray, а не «последней»: сборка повторяется.
mobile=$(awk '$1 == "golang.org/x/mobile" { print $2 }' "$tmp/src/go.mod")
[ -n "$mobile" ] || { echo "В go.mod libXray нет golang.org/x/mobile" >&2; exit 1; }
GOBIN="$tmp/bin" go install "golang.org/x/mobile/cmd/gomobile@$mobile" "golang.org/x/mobile/cmd/gobind@$mobile"
export PATH="$tmp/bin:$PATH"

mkdir -p "$dest"
# Как сборка самого libXray (build/app/android.py): -checklinkname=0 нужен Xray-core, страницы 16 КБ — Android 15+;
# нижний API — minSdk приложения (28).
(
  cd "$tmp/src"
  gomobile init
  gomobile bind -target android -androidapi 28 -trimpath \
    -ldflags="-checklinkname=0 -s -w -buildid= -extldflags=-Wl,-z,max-page-size=16384" \
    -o "$dest/libXray.aar" .
)
rm -f "$dest/libXray-sources.jar"
echo "libXray $commit → $dest/libXray.aar"
