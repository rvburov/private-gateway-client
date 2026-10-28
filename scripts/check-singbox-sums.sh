#!/usr/bin/env bash
# Сверка всех закрепленных сумм shared/cores/sing-box.sha256 с архивами выпусков на GitHub — и тех ОС, на которых CI
# sing-box не берет (Mac с Intel, Linux на ARM). Это вторая машина для суммы: первая — та, где строку добавили.
# Запуск: scripts/check-singbox-sums.sh (CI — в задании «Стиль и формат»).
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

. "$root/scripts/download.sh"
sum() { if command -v sha256sum > /dev/null; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1; }

failed=0
while read -r version asset pinned; do
  case "$version" in '' | '#'*) continue ;; esac
  download "https://github.com/SagerNet/sing-box/releases/download/$version/$asset" "$tmp/$asset"
  actual=$(sum "$tmp/$asset")
  if [ "$actual" = "$pinned" ]; then
    echo "OK $version $asset"
  else
    echo "Сумма $version $asset не совпала: закреплена $pinned, у архива $actual" >&2
    failed=1
  fi
  rm -f "$tmp/$asset"
done < "$root/shared/cores/sing-box.sha256"
exit "$failed"
