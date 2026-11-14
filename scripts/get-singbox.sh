#!/usr/bin/env bash
# sing-box — TUN режима VPN (отдельный процесс, GPL-3.0): версия — из shared/cores/sing-box.version,
# архив выпуска сверяется по SHA-256 с закрепленной суммой из shared/cores/sing-box.sha256 — без нее
# сборка не берется. Распаковка — в artifacts/cores/sing-box/ вместе с LICENSE. Запуск: scripts/get-singbox.sh
# macOS по умолчанию (и явно --build) собирает тот же тег и закрепленный коммит нативно с deployment target 13.0;
# метаданные готового ядра не переписываются. Нужны git, go и Apple clang; зависимости проверяются исходным go.sum.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
version=$(tr -d '[:space:]' < "$root/shared/cores/sing-box.version")
number=${version#v}
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) asset=sing-box-$number-linux-amd64.tar.gz ;;
  Linux-aarch64) asset=sing-box-$number-linux-arm64.tar.gz ;;
  Darwin-arm64) asset=sing-box-$number-darwin-arm64.tar.gz; build_arch=arm64 ;;
  Darwin-x86_64) asset=sing-box-$number-darwin-amd64.tar.gz; build_arch=amd64 ;;
  MINGW*-x86_64 | MSYS*-x86_64 | CYGWIN*-x86_64) asset=sing-box-$number-windows-amd64.zip ;;
  *) echo "Нет сборки sing-box для $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

dest="$root/artifacts/cores/sing-box"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

mode=${1:-}
if [ -z "$mode" ] && [ "$(uname -s)" = Darwin ]; then mode=--build; fi
if [ "$mode" = --build ]; then
  case "$(uname -s)" in Darwin) ;; *) echo "Сборка sing-box --build предназначена для macOS" >&2; exit 1 ;; esac
  command -v go > /dev/null || { echo "Нет go — собрать sing-box нельзя" >&2; exit 1; }
  command -v clang > /dev/null || { echo "Нет Apple clang — собрать sing-box нельзя" >&2; exit 1; }
  commit=$(tr -d '[:space:]' < "$root/shared/cores/sing-box.commit")
  [[ "$commit" =~ ^[0-9a-f]{40}$ ]] || { echo "Нет закрепленного коммита sing-box" >&2; exit 1; }
  git -c advice.detachedHead=false clone -q --depth 1 --branch "$version" https://github.com/SagerNet/sing-box "$tmp/src"
  [ "$(git -C "$tmp/src" rev-parse HEAD)" = "$commit" ] || { echo "Тег sing-box не совпал с закрепленным коммитом" >&2; exit 1; }
  tags=$(tr -d '[:space:]' < "$tmp/src/release/DEFAULT_BUILD_TAGS_OTHERS")
  shared_flags=$(cat "$tmp/src/release/LDFLAGS")
  mkdir -p "$tmp/build"
  (
    cd "$tmp/src"
    # CGO оставлен: системные сертификаты, DNS и нативные возможности не заменяются урезанным комплектом.
    # Нужна настоящая сборка для прежней ОС, не патч Mach-O. Явные флаги не наследуют minOS новой машины CI.
    export GOOS=darwin GOARCH="$build_arch" CGO_ENABLED=1 MACOSX_DEPLOYMENT_TARGET=13.0 CC=clang CXX=clang++ GOSUMDB=sum.golang.org
    export CGO_CFLAGS=-mmacosx-version-min=13.0 CGO_CXXFLAGS=-mmacosx-version-min=13.0 CGO_LDFLAGS=-mmacosx-version-min=13.0
    go build -mod=readonly -trimpath -buildvcs=false -tags "$tags" \
      -ldflags "-X github.com/sagernet/sing-box/constant.Version=$number $shared_flags -s -w -buildid=" \
      -o "$tmp/build/sing-box" ./cmd/sing-box
    go mod verify
    git diff --exit-code -- go.mod go.sum
  )
  cp "$tmp/src/LICENSE" "$tmp/build/LICENSE"
  cat > "$tmp/build/BUILD-INFO.txt" << EOF
Source: https://github.com/SagerNet/sing-box/tree/$commit
Version: $version
Commit: $commit
Build tags: $tags
CGO_ENABLED=1; MACOSX_DEPLOYMENT_TARGET=13.0
GOOS=darwin; GOARCH=$build_arch
Command: go build -mod=readonly -trimpath -buildvcs=false -tags "$tags" -ldflags "-X github.com/sagernet/sing-box/constant.Version=$number $shared_flags -s -w -buildid=" ./cmd/sing-box
Compiler: $(go version)
EOF
  rm -rf "$dest"
  mkdir -p "$dest"
  cp "$tmp/build/"* "$dest/"
  echo "sing-box $version ($commit), macOS 13.0, CGO → $dest"
  exit 0
elif [ -n "$mode" ]; then
  echo "Неизвестный параметр sing-box: $1" >&2
  exit 1
fi

pinned=$(grep -E "^$version $asset " "$root/shared/cores/sing-box.sha256" 2> /dev/null | cut -d' ' -f3 || true)
if [ -z "$pinned" ]; then
  echo "Для $version $asset нет закрепленной суммы в shared/cores/sing-box.sha256 — сборка не берется" >&2
  exit 1
fi

. "$root/scripts/download.sh"
download "https://github.com/SagerNet/sing-box/releases/download/$version/$asset" "$tmp/$asset"
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
