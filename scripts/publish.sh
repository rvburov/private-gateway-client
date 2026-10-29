#!/usr/bin/env bash
# Сборка выпуска для ОС этой машины: окно и служба (самодостаточные — .NET в комплекте, ReadyToRun), ядра Xray и
# sing-box только с закрепленными суммами, SBOM (CycloneDX), архив и SHA256SUMS. Запуск из корня:
#
#   scripts/publish.sh [версия]       # версия — по умолчанию <Version> из Directory.Build.props
#
# Папка сборки — artifacts/publish/PrivateGateway-<версия>-<rid>/ (так ее раскладывает установщик: служба рядом с окном,
# ядра в cores/), выпуск — artifacts/release/ (scripts/package.cs). Workflow publish.yml запускает его на трех ОС.
# Ядра качаются заново: в выпуск не попадает сборка из исходников (get-xray.sh --build) или чужая из artifacts/cores.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"
version=${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)}
if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "Версия «$version» — не SemVer (1.2.3 или 1.2.3-beta.1)" >&2
  exit 1
fi
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) rid=linux-x64 ;;
  Linux-aarch64) rid=linux-arm64 ;;
  Darwin-x86_64) rid=osx-x64 ;;
  Darwin-arm64) rid=osx-arm64 ;;
  MINGW*-x86_64 | MSYS*-x86_64 | CYGWIN*-x86_64) rid=win-x64 ;;
  *) echo "Нет сборки выпуска для $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

name="PrivateGateway-$version-$rid"
out="artifacts/publish/$name"
rm -rf "$out" artifacts/release
mkdir -p "$out"

# Время в SBOM — время коммита: две сборки одного коммита дают одинаковое время.
export SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-$(git log -1 --format=%ct)}

for project in Client.UI Client.Service; do
  dotnet publish "desktop/src/$project" -c Release -r "$rid" --self-contained -p:Version="$version" -p:PublishReadyToRun=true -o "$out"
done

# Отладочные символы и XML-документация своих сборок пользователю не нужны.
find "$out" -maxdepth 1 \( -name '*.pdb' -o -name 'PrivateGateway*.xml' \) -delete

scripts/get-xray.sh --pinned
scripts/get-singbox.sh
mkdir -p "$out/cores"
cp -R artifacts/cores/xray artifacts/cores/sing-box "$out/cores/"

dotnet run scripts/package.cs "$out" artifacts/release "$version" "$rid"

echo
echo "Сборка выпуска готова: $out"
echo "Архив и суммы: artifacts/release"
case "$rid" in
  win-*) echo "Установщик Windows (MSI): scripts/package-windows.sh $version" ;;
  osx-*) echo "Образ диска macOS (.dmg): scripts/package-macos.sh $version" ;;
  linux-*) echo "Пакеты Linux (.deb и .rpm): scripts/package-linux.sh $version" ;;
esac
