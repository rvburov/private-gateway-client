#!/usr/bin/env bash
# Проект Xcode приложения iOS из ios/project.yml — XcodeGen закрепленной версии, сумма архива сверяется. Нужен macOS.
# Результат — ios/PrivateGateway.xcodeproj и Info.plist/entitlements целей (в git не хранятся). XcodeGen кладется в
# artifacts/tools и в приложение не попадает. Запуск: scripts/ios-project.sh
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
version=2.46.0
sha256=4d9e34b62172d645eed6457cac13fc222569974098ef4ee9c3368bedf0196806
tools="$root/artifacts/tools/xcodegen-$version"
xcodegen="$tools/xcodegen/bin/xcodegen"

if [ ! -x "$xcodegen" ]; then
  mkdir -p "$tools"
  . "$root/scripts/download.sh"
  download "https://github.com/yonaskolb/XcodeGen/releases/download/$version/xcodegen.zip" "$tools/xcodegen.zip"
  if ! echo "$sha256  $tools/xcodegen.zip" | shasum -a 256 -c - > /dev/null; then
    echo "Сумма архива XcodeGen $version не совпала — архив не используется" >&2
    rm -f "$tools/xcodegen.zip"
    exit 1
  fi
  unzip -q -o "$tools/xcodegen.zip" -d "$tools"
fi

# Один источник отображаемой версии для десктопа, Android, приложения iOS и его расширения.
PG_APP_VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -1)
[[ "$PG_APP_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Некорректная общая версия: $PG_APP_VERSION" >&2; exit 1; }
export PG_APP_VERSION
"$xcodegen" generate --spec "$root/ios/project.yml" --quiet
echo "Проект: ios/PrivateGateway.xcodeproj (XcodeGen $version)"
