#!/usr/bin/env bash
# Установщик Windows (MSI) из сборки выпуска (scripts/publish.sh) — WiX v5 по desktop/packaging/windows/Product.wxs:
#
#   scripts/package-windows.sh <версия> [версия MSI]      # в Git Bash
#
# Берет artifacts/publish/PrivateGateway-<версия>-win-x64, кладет PrivateGateway-<версия>-win-x64.msi в artifacts/release
# и пересчитывает SHA256SUMS. Нужен wix 5.0.2 (dotnet tool install --global wix --version 5.0.2); расширения Util и UI
# (мастер установки) скрипт ставит сам. Версия MSI — только цифры: 1.2.3-beta.1 → 1.2.3.
# Своя версия MSI — для проверки обновления (windows-installer-smoke.sh): та же сборка, установщик «новее»; он кладется в
# artifacts/msi-<версия MSI>.msi, не в выпуск.
set -euo pipefail

version=${1:?версия выпуска}
msi_version=${2:-${version%%-*}}
cd "$(dirname "$0")/.."
build="artifacts/publish/PrivateGateway-$version-win-x64"
[ -d "$build" ] || { echo "Нет сборки $build — сначала scripts/publish.sh $version" >&2; exit 1; }
command -v wix > /dev/null || { echo "Нет wix: dotnet tool install --global wix --version 5.0.2" >&2; exit 1; }
# Расширения WiX: Util — команды службы и запуск окна после установки, UI — мастер установки. Уже стоят — ничего не делает.
# Ошибку добавления не прячем совсем: без расширения wix build ниже назовет, чего нет.
for ext in WixToolset.Util.wixext WixToolset.UI.wixext; do
  wix extension add --global "$ext/5.0.2" > /dev/null 2>&1 || true
done

# Пути — полные: относительные WiX считает от папки .wxs. -wx: предупреждение (например, «нечего собирать» у Files) — ошибка,
# иначе пустой установщик собрался бы молча.
out="artifacts/release/PrivateGateway-$version-win-x64.msi"
[ -z "${2:-}" ] || out="artifacts/msi-$msi_version.msi"
wix build desktop/packaging/windows/Product.wxs -arch x64 -wx -ext WixToolset.Util.wixext -ext WixToolset.UI.wixext -culture ru-RU \
  -d "Version=$msi_version" -d "Icon=$(cygpath -m "$PWD")/shared/specifications/icons/app.ico" -bindpath "App=$(cygpath -m "$PWD")/$build" \
  -o "$out"
rm -f artifacts/release/*.wixpdb artifacts/*.wixpdb
[ -z "${2:-}" ] || { echo "$out"; exit 0; }

cd artifacts/release
sha256sum $(LC_ALL=C ls | grep -v "^SHA256SUMS$") > SHA256SUMS
cat SHA256SUMS
echo
echo "Установщик готов: $(cygpath -w "$PWD/PrivateGateway-$version-win-x64.msi")"
