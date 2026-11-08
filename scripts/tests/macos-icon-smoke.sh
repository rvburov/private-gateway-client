#!/usr/bin/env bash
# Проверка ICNS штатным декодером macOS. Без установки программы, запуска службы и изменений сети.
# Без аргумента собирает значок из app.iconset; с аргументом проверяет готовый ICNS, в том числе из пакета.
set -euo pipefail
cd "$(dirname "$0")/../.."
[[ $(uname -s) == Darwin ]] || { echo "Нужна macOS: iconutil и ImageIO" >&2; exit 1; }
pg_icon_temp=$(mktemp -d "${TMPDIR:-/tmp}/pg-icon-check.XXXXXX")
trap 'rm -rf "$pg_icon_temp"' EXIT
pg_icon_source=shared/specifications/icons/app.iconset
pg_icon_file=${1:-$pg_icon_temp/app.icns}
if [[ $# -eq 0 ]]; then
  iconutil -c icns "$pg_icon_source" -o "$pg_icon_file"
fi
iconutil -c iconset "$pg_icon_file" -o "$pg_icon_temp/decoded.iconset"
swift scripts/tests/macos-icon-compare.swift "$pg_icon_source" "$pg_icon_temp/decoded.iconset"
