#!/usr/bin/env bash
# sing-box — TUN режима VPN (отдельный процесс, GPL-3.0): версия — из shared/cores/sing-box.version,
# архив выпуска сверяется по SHA-256 с закрепленной суммой из shared/cores/sing-box.sha256 — без нее
# сборка не берется. Распаковка — в artifacts/cores/sing-box/ вместе с LICENSE. Запуск: scripts/get-singbox.sh
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
version=$(tr -d '[:space:]' < "$root/shared/cores/sing-box.version")
number=${version#v}
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) asset=sing-box-$number-linux-amd64.tar.gz ;;
  Linux-aarch64) asset=sing-box-$number-linux-arm64.tar.gz ;;
  Darwin-arm64) asset=sing-box-$number-darwin-arm64.tar.gz ;;
  Darwin-x86_64) asset=sing-box-$number-darwin-amd64.tar.gz ;;
  MINGW*-x86_64 | MSYS*-x86_64 | CYGWIN*-x86_64) asset=sing-box-$number-windows-amd64.zip ;;
  *) echo "Нет сборки sing-box для $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

pinned=$(grep -E "^$version $asset " "$root/shared/cores/sing-box.sha256" 2> /dev/null | cut -d' ' -f3 || true)
if [ -z "$pinned" ]; then
  echo "Для $version $asset нет закрепленной суммы в shared/cores/sing-box.sha256 — сборка не берется" >&2
  exit 1
fi

dest="$root/artifacts/cores/sing-box"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

curl -fsSL --retry 3 -o "$tmp/$asset" "https://github.com/SagerNet/sing-box/releases/download/$version/$asset"
if command -v sha256sum > /dev/null; then
  actual=$(sha256sum "$tmp/$asset" | cut -d' ' -f1)
else
  actual=$(shasum -a 256 "$tmp/$asset" | cut -d' ' -f1)
fi
if [ "$actual" != "$pinned" ]; then
  echo "SHA-256 $asset не совпала с закрепленной в shared/cores/sing-box.sha256: $actual ≠ $pinned" >&2
  exit 1
fi

mkdir -p "$tmp/x"
case "$asset" in
  *.zip)
    if command -v unzip > /dev/null; then
      unzip -q -o "$tmp/$asset" -d "$tmp/x"
    else
      python3 -m zipfile -e "$tmp/$asset" "$tmp/x" 2> /dev/null || python -m zipfile -e "$tmp/$asset" "$tmp/x"
    fi ;;
  *) tar -xzf "$tmp/$asset" -C "$tmp/x" ;;
esac

# В архиве — папка sing-box-<версия>-<ОС>: ядро и LICENSE; libcronet (выход naive) не нужен.
rm -rf "$dest"
mkdir -p "$dest"
src=$(find "$tmp/x" -mindepth 1 -maxdepth 1 -type d | head -1)
cp "$src"/sing-box* "$src/LICENSE" "$dest/"
chmod +x "$dest"/sing-box* 2> /dev/null || true

echo "sing-box $version ($asset) → $dest"
echo "SHA-256: $version $asset $actual (закреплена)"
