#!/usr/bin/env bash
# Программа macOS из сборки выпуска (scripts/publish.sh) — Private Gateway.app в образе диска:
#
#   scripts/package-macos.sh <версия>          # только на macOS: codesign и hdiutil
#
# Берет artifacts/publish/PrivateGateway-<версия>-<rid> (osx-arm64 или osx-x64 — по машине), собирает Private Gateway.app:
# Info.plist, значок из app.iconset (scripts/gen-icons.cs и iconutil), вся сборка — в Contents/MacOS (окно, служба рядом с ним, ядра в
# cores/, как у архива: окно ставит службу оттуда). Подпись — ad-hoc, без сертификата: на Apple Silicon код без подписи
# не запускается, а подпись пакета — печать его целостности (подпись Developer ID и нотаризация — «Отложено», README).
# Кладет PrivateGateway-<версия>-<rid>.dmg (программа и ярлык на «Программы») в artifacts/release и пересчитывает
# SHA256SUMS. Окно образа оформлено: фон с инструкцией, как поставить и открыть программу без подписи Apple
# (scripts/dmg-background.swift, RU и строка EN), значки на своих местах. Оформляет Finder через AppleScript; не вышло (нет
# графического сеанса) — образ остается рабочим, без инструкции, с предупреждением в журнале.
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

# Отдельная папка на каждую упаковку: уже открытая проверочная копия не теряет свои файлы.
mkdir -p artifacts/dmg
staging=$(mktemp -d "artifacts/dmg/PrivateGateway-$version-$rid.XXXXXX")
app="$staging/Private Gateway.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$build/." "$app/Contents/MacOS/"
# Apple формирует контейнер для маленьких размеров и Retina. Перед подписью проверяем именно упакованный файл.
iconutil -c icns shared/specifications/icons/app.iconset -o "$app/Contents/Resources/PrivateGateway.icns"
bash scripts/tests/macos-icon-smoke.sh "$app/Contents/Resources/PrivateGateway.icns"

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

# Фон окна с инструкцией: две плотности в одном TIFF, иначе на Retina текст выходит мылом.
mkdir -p "$app/Contents/Resources/Installer"
swift scripts/dmg-background.swift "$app/Contents/Resources/Installer" shared/specifications/design-tokens.json
tiffutil -cathidpicheck "$app/Contents/Resources/Installer/background.png" "$app/Contents/Resources/Installer/background@2x.png" \
  -out "$app/Contents/Resources/Installer/background-raw.tiff"
tiffutil -lzw "$app/Contents/Resources/Installer/background-raw.tiff" -out "$app/Contents/Resources/Installer/background.tiff"
rm "$app/Contents/Resources/Installer/background-raw.tiff"
rm "$app/Contents/Resources/Installer/background.png" "$app/Contents/Resources/Installer/background@2x.png"

# Сначала каждый файл Contents/MacOS, кроме главного исполняемого: codesign считает их вложенным кодом и требует подписи у
# всех, не только у Mach-O (у сборок .NET и данных она ложится в расширенные атрибуты файла). Главный исполняемый
# подписывается вместе с пакетом: его подпись запечатывает все файлы, и измененный файл macOS заметит.
find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/PrivateGateway" -print0 | xargs -0 codesign --force --sign -
codesign --force --sign - "$app"
codesign --verify --deep --strict --verbose=2 "$app"

ln -s /Applications "$staging/Applications"

mkdir -p artifacts/release
out="artifacts/release/PrivateGateway-$version-$rid.dmg"
rm -f "$out"

# Образ — в два приема: записываемый (UDRW), чтобы Finder разложил окно, потом сжатый только для чтения (UDZO) — в выпуск.
# Имя тома — заголовок окна, по нему же Finder находит диск.
volume="Private Gateway $version"
rw="artifacts/dmg/PrivateGateway-$version-$rid-rw.dmg"
rm -f "$rw"
hdiutil create -quiet -volname "$volume" -srcfolder "$staging" -fs HFS+ -format UDRW -ov "$rw"

# Отключить образ по устройству (/dev/diskN из вывода attach), а не по пути тома. Finder после оформления окна и
# индексатор Spotlight держат его еще несколько секунд: hdiutil отвечает «Resource busy» (код 16; так упала сборка
# 2026-11-05) и бывает, что том уже отмонтирован, а диск еще не отключен — тогда пути тома нет, и повтор по пути падал
# с «No such file or directory» (сборка выпуска master 2026-11-05). Пропало устройство — образ отключен.
detach() {
  local attempt
  for attempt in 1 2 3 4 5; do
    [ -e "$1" ] || return 0
    hdiutil detach "$1" && return 0
    echo "Образ $1 занят — повтор через $((attempt * 2)) с" >&2
    sleep $((attempt * 2))
  done
  [ -e "$1" ] || return 0
  hdiutil detach -force "$1"
}

# Подключить образ и вернуть его устройство /dev/diskN.
attach() {
  local device
  device=$(hdiutil attach "$@" | grep -o '^/dev/disk[0-9]*' | head -1)
  [ -n "$device" ] || { echo "hdiutil attach не назвал устройство образа" >&2; return 1; }
  echo "$device"
}

mount="/Volumes/$volume"
device=$(attach -noautoopen -mountpoint "$mount" "$rw")
osascript - "$volume" << 'APPLESCRIPT' || echo "::warning::Finder не оформил окно образа — образ собран без инструкции"
on run argv
tell application "Finder"
  tell disk (item 1 of argv)
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {200, 120, 920, 750}
    set opts to the icon view options of container window
    set arrangement of opts to not arranged
    set icon size of opts to 112
    set text size of opts to 13
    set label position of opts to bottom
    set background picture of opts to file "Private Gateway.app:Contents:Resources:Installer:background.tiff"
    set position of item "Private Gateway.app" of container window to {200, 120}
    set position of item "Applications" of container window to {520, 120}
    close
    open
    update without registering applications
    delay 2
    close
  end tell
end tell
end run
APPLESCRIPT
# Переносим только оформление Finder. Журнал .fseventsd принадлежит временному записываемому тому.
# Чистый финальный образ создается сразу только для чтения: служебные папки в него не попадают.
sync
if [ -f "$mount/.DS_Store" ]; then cp "$mount/.DS_Store" "$staging/.DS_Store"; fi
detach "$device"
hdiutil create -quiet -volname "$volume" -srcfolder "$staging" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$out"
rm -f "$rw"

# Контрольное монтирование: программа, ярлык «Программы» и фон на месте, подпись пережила упаковку.
check=$(mktemp -d)
device=$(attach -nobrowse -readonly -mountpoint "$check" "$out")
problem=""
[ -d "$check/Private Gateway.app" ] || problem="в образе нет Private Gateway.app"
[ "$(readlink "$check/Applications")" = /Applications ] || problem="в образе нет ярлыка на «Программы»"
[ -f "$check/Private Gateway.app/Contents/Resources/Installer/background.tiff" ] || problem="в образе нет фона с инструкцией"
[ ! -e "$check/.background" ] && [ ! -e "$check/.fseventsd" ] || problem="в корне образа остались служебные папки"
codesign --verify --deep --strict "$check/Private Gateway.app" || problem="подпись программы не пережила упаковку"
[ -f "$check/.DS_Store" ] || echo "::warning::в образе нет .DS_Store — окно откроется без оформления"
detach "$device"
rmdir "$check"
[ -z "$problem" ] || { echo "Образ диска: $problem" >&2; exit 1; }

cd artifacts/release
shasum -a 256 $(LC_ALL=C ls | grep -v "^SHA256SUMS$") > SHA256SUMS
cat SHA256SUMS
echo
echo "Образ диска готов: $PWD/PrivateGateway-$version-$rid.dmg — открыть и перетащить Private Gateway в «Программы»"
