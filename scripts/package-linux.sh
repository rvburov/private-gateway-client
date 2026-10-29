#!/usr/bin/env bash
# Пакеты Linux .deb и .rpm из сборки выпуска (scripts/publish.sh) — nfpm по desktop/packaging/linux/nfpm.yaml:
#
#   scripts/package-linux.sh <версия>
#
# Берет artifacts/publish/PrivateGateway-<версия>-linux-x64, кладет private-gateway_<версия>_amd64.deb и
# private-gateway-<версия>.x86_64.rpm в artifacts/release и пересчитывает SHA256SUMS. Нужен nfpm (CI ставит закрепленную
# версию: go install github.com/goreleaser/nfpm/v2/cmd/nfpm@v2.47.0 — модуль сверяется по sum.golang.org).
set -euo pipefail

version=${1:?версия выпуска}
cd "$(dirname "$0")/.."
build="artifacts/publish/PrivateGateway-$version-linux-x64"
[ -d "$build" ] || { echo "Нет сборки $build — сначала scripts/publish.sh $version" >&2; exit 1; }
command -v nfpm > /dev/null || { echo "Нет nfpm: go install github.com/goreleaser/nfpm/v2/cmd/nfpm@v2.47.0" >&2; exit 1; }

# nfpm подставляет окружение не во все поля (в contents.src — нет): BUILD и VERSION — во временную копию конфига.
config=artifacts/nfpm.yaml
sed -e "s|\${BUILD}|$build|g" -e "s|\${VERSION}|$version|g" desktop/packaging/linux/nfpm.yaml > "$config"
nfpm package --config "$config" --packager deb --target artifacts/release/
nfpm package --config "$config" --packager rpm --target artifacts/release/

cd artifacts/release
sha256sum $(LC_ALL=C ls | grep -v "^SHA256SUMS$") > SHA256SUMS
cat SHA256SUMS
echo
echo "Пакеты готовы: $PWD — .deb: sudo apt install ./<файл>.deb, .rpm: sudo dnf install ./<файл>.rpm"
