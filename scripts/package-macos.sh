#!/usr/bin/env bash
# Программа macOS из сборки выпуска (scripts/publish.sh) — Private Gateway.app в образе диска:
#
#   scripts/package-macos.sh <версия>          # только на macOS: codesign и hdiutil
#
# Берет artifacts/publish/PrivateGateway-<версия>-<rid> (osx-arm64 или osx-x64 — по машине), собирает Private Gateway.app:
# Info.plist, значок app.icns (scripts/gen-icons.cs), вся сборка — в Contents/MacOS (окно, служба рядом с ним, ядра в
# cores/, как у архива: окно ставит службу оттуда). Подпись — ad-hoc, без сертификата: на Apple Silicon код без подписи
# не запускается, а подпись пакета — печать его целостности (подпись Developer ID и нотаризация — «Отложено», README).
# Кладет PrivateGateway-<версия>-<rid>.dmg (программа и ярлык на «Программы») в artifacts/release и пересчитывает
# SHA256SUMS.
set -euo pipefail

version=${1:?версия выпуска}
cd "$(dirname "$0")/.."
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) rid=osx-arm64 ;;
  Darwin-x86_64) rid=osx-x64 ;;
  *) echo "Пакет macOS собирается только на macOS" >&2; exit 1 ;;
esac
build="artifacts/publish/PrivateGateway-$version-$rid"
[ -d "$build" ] || { echo "Нет сборки $build — сначала scripts/publish.sh $version" >&2; exit 1; }

staging="artifacts/dmg/PrivateGateway-$version-$rid"
app="$staging/Private Gateway.app"
rm -rf "$staging"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$build/." "$app/Contents/MacOS/"
cp shared/specifications/icons/app.icns "$app/Contents/Resources/PrivateGateway.icns"

# Версия пакета — только цифры: 1.2.3-beta.1 → 1.2.3. Идентификатор — обратный домен репозитория, как метка службы
# launchd. Минимальная macOS — как у .NET 10.
short=${version%%-*}
cat > "$app/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>ru</string>
  <key>CFBundleDisplayName</key>
  <string>Private Gateway</string>
  <key>CFBundleExecutable</key>
  <string>PrivateGateway</string>
  <key>CFBundleIconFile</key>
  <string>PrivateGateway</string>
  <key>CFBundleIdentifier</key>
  <string>io.github.rvburov.private-gateway</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>Private Gateway</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$short</string>
  <key>CFBundleVersion</key>
  <string>$short</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
  <key>LSMinimumSystemVersion</key>
  <string>14.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
EOF
plutil -lint "$app/Contents/Info.plist"

# Сначала каждый файл Contents/MacOS: codesign считает их вложенным кодом и требует подписи у всех, не только у Mach-O
# (у сборок .NET и данных она ложится в расширенные атрибуты файла). Потом пакет целиком: его подпись запечатывает все
# файлы, и измененный файл macOS заметит.
find "$app/Contents/MacOS" -type f -print0 | xargs -0 codesign --force --sign -
codesign --force --sign - "$app"
codesign --verify --deep --strict --verbose=2 "$app"

ln -s /Applications "$staging/Applications"
mkdir -p artifacts/release
out="artifacts/release/PrivateGateway-$version-$rid.dmg"
rm -f "$out"
hdiutil create -volname "Private Gateway" -srcfolder "$staging" -fs HFS+ -format UDZO -ov "$out"

cd artifacts/release
shasum -a 256 $(LC_ALL=C ls | grep -v "^SHA256SUMS$") > SHA256SUMS
cat SHA256SUMS
