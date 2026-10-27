#!/usr/bin/env bash
# Ядро Xray для iOS: libXray (XTLS, MIT) — Xray-core в LibXray.xcframework через gomobile, для iPhone и симулятора.
# Коммит libXray — общий с Android (android/libxray.version): ядро на обоих телефонах одно; версия Xray-core и всех модулей
# Go — из go.mod и go.sum этого коммита (go сверяет суммы при загрузке). Нужны macOS с Xcode, git и go.
# Результат — ios/Frameworks/LibXray.xcframework и LibXray.version с коммитом (в git не хранятся); scripts/ios-test.sh
# собирает заново, когда коммит в android/libxray.version другой. Запуск: scripts/get-libxray-ios.sh
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
commit=$(tr -d '[:space:]' < "$root/android/libxray.version")
dest="$root/ios/Frameworks"

[ "$(uname)" = Darwin ] || { echo "LibXray.xcframework собирается только на macOS с Xcode" >&2; exit 1; }
command -v go > /dev/null || { echo "Нет go — libXray не собрать" >&2; exit 1; }
command -v xcrun > /dev/null || { echo "Нет Xcode — libXray не собрать" >&2; exit 1; }

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
rm -rf "$dest/LibXray.xcframework" "$dest/LibXray.version"
# Как сборка самого libXray (build/app/apple_gomobile.py): -checklinkname=0 нужен Xray-core; нижняя версия — iOS 16, как
# у приложения; только iPhone и симулятор — macOS и Mac Catalyst приложению не нужны.
(
  cd "$tmp/src"
  gomobile init
  gomobile bind -target ios,iossimulator -iosversion 16.0 -trimpath \
    -ldflags="-checklinkname=0 -s -w -buildid=" \
    -o "$dest/LibXray.xcframework" .
)
echo "$commit" > "$dest/LibXray.version"
echo "libXray $commit → $dest/LibXray.xcframework"
