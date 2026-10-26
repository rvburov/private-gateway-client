#!/bin/bash
#
# Проверка выпуска из artifacts/release (scripts/publish.sh): суммы SHA256SUMS сходятся, архив распаковывается, в нем
# окно, служба, ядра, геобаза сайтов (геобазы IP нет), SBOM и THIRD-PARTY-NOTICES, служба запускается без установленного
# .NET и называет свою версию, ядра запускаются, SBOM в архиве — тот же, что рядом с ним, и называет оба ядра,
# уведомления — с полными текстами лицензий ядер и геобазой. Запускается в publish.yml и вручную:
#
#   scripts/tests/release-smoke.sh <версия>
#
# Распаковывает во временную папку, она удаляется в конце.
set -euo pipefail

version=${1:?версия выпуска}
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
release=artifacts/release
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

fail() {
  echo "НЕ ТАК: $*" >&2
  exit 1
}

(cd "$release" && if command -v sha256sum > /dev/null; then sha256sum -c SHA256SUMS; else shasum -a 256 -c SHA256SUMS; fi) || fail "суммы SHA256SUMS"

archive=
for candidate in "$release"/PrivateGateway-"$version"-*.zip "$release"/PrivateGateway-"$version"-*.tar.gz; do
  [ -f "$candidate" ] && archive=$candidate
done
[ -n "$archive" ] || fail "нет архива PrivateGateway-$version-* в $release"
case "$archive" in
  *.zip) if command -v unzip > /dev/null; then unzip -q "$archive" -d "$tmp"; else python3 -m zipfile -e "$archive" "$tmp" 2> /dev/null || python -m zipfile -e "$archive" "$tmp"; fi ;;
  *) tar -xzf "$archive" -C "$tmp" ;;
esac
name=$(basename "$archive" | sed -E 's/\.(zip|tar\.gz)$//')
dir="$tmp/$name"
[ -d "$dir" ] || fail "в архиве нет папки $name"

exe=
case "$name" in *-win-*) exe=.exe ;; esac
for file in "PrivateGateway$exe" "PrivateGateway.Service$exe" "cores/xray/xray$exe" cores/xray/geosite.dat \
  "cores/sing-box/sing-box$exe" cores/sing-box/LICENSE sbom.cdx.json THIRD-PARTY-NOTICES.txt; do
  [ -f "$dir/$file" ] || fail "в архиве нет $file"
done
[ ! -e "$dir/cores/xray/geoip.dat" ] || fail "в архиве geoip.dat: локальные сети — списком в конфиге, геобаза IP не нужна"
if [ -z "$exe" ]; then
  for file in PrivateGateway PrivateGateway.Service cores/xray/xray cores/sing-box/sing-box; do
    [ -x "$dir/$file" ] || fail "$file в архиве без права на запуск"
  done
fi

# Служба — самодостаточная: без DOTNET_ROOT и dotnet в PATH она все равно запускается.
service=$(env -u DOTNET_ROOT PATH=/usr/bin:/bin "$dir/PrivateGateway.Service$exe" --version) || fail "служба из архива не запускается"
[ "$service" = "Private Gateway Service $version (IPC v1)" ] || fail "служба называет себя «$service», ждали версию $version"
echo "ок: $service"
"$dir/cores/xray/xray$exe" version | head -1
"$dir/cores/sing-box/sing-box$exe" version | head -1

cmp -s "$dir/sbom.cdx.json" "$release/$name.cdx.json" || fail "SBOM в архиве и рядом с ним разные"
for core in '"name": "Xray-core"' '"name": "sing-box"' '"name": "Avalonia"' '"bomFormat": "CycloneDX"'; do
  grep -qF "$core" "$dir/sbom.cdx.json" || fail "в SBOM нет $core"
done
for notice in 'sing-box v' 'Исходный код / Source code: https://github.com/SagerNet/sing-box/tree/v' 'GNU GENERAL PUBLIC LICENSE' \
  'Mozilla Public License Version 2.0' 'JetBrains Mono' 'geosite.dat'; do
  grep -qF "$notice" "$dir/THIRD-PARTY-NOTICES.txt" || fail "в THIRD-PARTY-NOTICES нет «$notice»"
done
echo "Выпуск $name проверен"
