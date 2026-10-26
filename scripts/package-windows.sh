#!/usr/bin/env bash
# Установщик Windows (MSI) из сборки выпуска (scripts/publish.sh) — WiX v5 по desktop/packaging/windows/Product.wxs:
#
#   scripts/package-windows.sh <версия>      # в Git Bash
#
# Берет artifacts/publish/PrivateGateway-<версия>-win-x64, кладет PrivateGateway-<версия>-win-x64.msi в artifacts/release
# и пересчитывает SHA256SUMS. Нужен wix 5.0.2 с расширением Util (CI: dotnet tool install --global wix --version 5.0.2;
# wix extension add --global WixToolset.Util.wixext/5.0.2). Версия MSI — только цифры: 1.2.3-beta.1 → 1.2.3.
set -euo pipefail

version=${1:?версия выпуска}
cd "$(dirname "$0")/.."
build="artifacts/publish/PrivateGateway-$version-win-x64"
[ -d "$build" ] || { echo "Нет сборки $build — сначала scripts/publish.sh $version" >&2; exit 1; }
command -v wix > /dev/null || { echo "Нет wix: dotnet tool install --global wix --version 5.0.2" >&2; exit 1; }

# Пути — полные: относительные WiX считает от папки .wxs. -wx: предупреждение (например, «нечего собирать» у Files) — ошибка,
# иначе пустой установщик собрался бы молча.
wix build desktop/packaging/windows/Product.wxs -arch x64 -wx -ext WixToolset.Util.wixext \
  -d "Version=${version%%-*}" -d "Icon=$(cygpath -m "$PWD")/shared/specifications/icons/app.ico" -bindpath "App=$(cygpath -m "$PWD")/$build" \
  -o "artifacts/release/PrivateGateway-$version-win-x64.msi"
rm -f artifacts/release/*.wixpdb

cd artifacts/release
sha256sum $(LC_ALL=C ls | grep -v "^SHA256SUMS$") > SHA256SUMS
cat SHA256SUMS
