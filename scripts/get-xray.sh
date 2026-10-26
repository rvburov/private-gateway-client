#!/usr/bin/env bash
# Сборка Xray-core для тестов и разработки: версия — из shared/cores/xray.version, архив сверяется по
# SHA-256 с закрепленной суммой из shared/cores/xray.sha256 (без нее — с предупреждением) и с .dgst
# того же выпуска. Распаковка — в artifacts/cores/xray/. Запуск: scripts/get-xray.sh
# Для выпуска (scripts/publish.sh) — scripts/get-xray.sh --pinned: без закрепленной суммы — ошибка, а не предупреждение.
# В облаке без доступа к выпускам GitHub — scripts/get-xray.sh --build (сборка из исходников тега).
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
version=$(tr -d '[:space:]' < "$root/shared/cores/xray.version")
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) asset=Xray-linux-64.zip ;;
  Linux-aarch64) asset=Xray-linux-arm64-v8a.zip ;;
  Darwin-x86_64) asset=Xray-macos-64.zip ;;
  Darwin-arm64) asset=Xray-macos-arm64-v8a.zip ;;
  MINGW*-x86_64 | MSYS*-x86_64 | CYGWIN*-x86_64) asset=Xray-windows-64.zip ;;
  *) echo "Нет сборки Xray для $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

dest="$root/artifacts/cores/xray"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

if [ "${1:-}" = "--build" ]; then
  # Облако без доступа к выпускам GitHub: сборка из исходников тега (нужны git и go), геобаза сайтов — из ветки
  # release Loyalsoldier/v2ray-rules-dat, ее же кладет в архив выпуск Xray. Только для тестов: суммы
  # выпуска к такой сборке не относятся.
  command -v go > /dev/null || { echo "Нет go — собрать Xray из исходников нельзя" >&2; exit 1; }
  git -c advice.detachedHead=false clone -q --depth 1 --branch "$version" https://github.com/XTLS/Xray-core "$tmp/src"
  git clone -q --depth 1 --branch release https://github.com/Loyalsoldier/v2ray-rules-dat "$tmp/geo"
  rm -rf "$dest"
  mkdir -p "$dest"
  (cd "$tmp/src" && CGO_ENABLED=0 go build -o "$dest/xray$(go env GOEXE)" -trimpath -buildvcs=false -ldflags="-s -w -buildid=" ./main)
  cp "$tmp/geo/geosite.dat" "$dest/"
  echo "Xray $version собран из исходников → $dest (только для тестов)"
  exit 0
fi

base="https://github.com/XTLS/Xray-core/releases/download/$version"

curl -fsSL --retry 3 -o "$tmp/$asset" "$base/$asset"
curl -fsSL --retry 3 -o "$tmp/$asset.dgst" "$base/$asset.dgst"

if command -v sha256sum > /dev/null; then
  actual=$(sha256sum "$tmp/$asset" | cut -d' ' -f1)
else
  actual=$(shasum -a 256 "$tmp/$asset" | cut -d' ' -f1)
fi

published=$(grep -iE '^SHA(2-)?256=' "$tmp/$asset.dgst" | head -1 | cut -d'=' -f2 | tr -d '[:space:]' | tr 'A-F' 'a-f')
if [ "$actual" != "$published" ]; then
  echo "SHA-256 $asset не совпала с .dgst выпуска: $actual ≠ $published" >&2
  exit 1
fi

pinned=$(grep -E "^$version $asset " "$root/shared/cores/xray.sha256" 2> /dev/null | cut -d' ' -f3 || true)
if [ -z "$pinned" ] && [ "${1:-}" = "--pinned" ]; then
  echo "Для $version $asset нет закрепленной суммы в shared/cores/xray.sha256 — в выпуск такая сборка не идет" >&2
  exit 1
elif [ -z "$pinned" ]; then
  echo "Внимание: для $version $asset нет закрепленной суммы в shared/cores/xray.sha256 — сверено только с .dgst выпуска" >&2
elif [ "$actual" != "$pinned" ]; then
  echo "SHA-256 $asset не совпала с закрепленной в shared/cores/xray.sha256: $actual ≠ $pinned" >&2
  exit 1
fi

rm -rf "$dest"
mkdir -p "$dest"
if command -v unzip > /dev/null; then
  unzip -q -o "$tmp/$asset" -d "$dest"
else
  python3 -m zipfile -e "$tmp/$asset" "$dest" 2> /dev/null || python -m zipfile -e "$tmp/$asset" "$dest"
fi
chmod +x "$dest/xray" 2> /dev/null || true
# geoip.dat не нужен: локальные сети служба пишет в конфиг списком (XrayConfigBuilder.LocalNetworks), а GeoIP в нем
# собран из данных MaxMind GeoLite2 со своими условиями. Без файла его не возит ни выпуск, ни тесты.
rm -f "$dest/geoip.dat"

echo "Xray $version ($asset) → $dest"
echo "SHA-256: $version $asset $actual ${pinned:+(закреплена)}"
