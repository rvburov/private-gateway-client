#!/usr/bin/env bash
# Геобаза сайтов для Xray (geosite.dat, правила «geosite:категория» в «Маршрутизации») — своя сборка из
# v2fly/domain-list-community (MIT) закрепленного коммита (shared/cores/geosite.version) его же генератором: нужны git и
# go, модули генератора сверяются по go.sum этого коммита. Готовую геобазу из архива Xray (сборка
# Loyalsoldier/v2ray-rules-dat, GPL-3.0, с данными из других списков) выпуск не возит (решение 361).
#
#   scripts/get-geosite.sh <папка>        # кладет <папка>/geosite.dat; publish.sh — в cores/xray
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
dest=${1:?папка для geosite.dat}
commit=$(tr -d '[:space:]' < "$root/shared/cores/geosite.version")
command -v git > /dev/null || { echo "Нет git — геобазу сайтов не собрать" >&2; exit 1; }
command -v go > /dev/null || { echo "Нет go — геобазу сайтов не собрать (CI: actions/setup-go)" >&2; exit 1; }

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
git -C "$tmp" init -q
git -C "$tmp" fetch -q --depth 1 https://github.com/v2fly/domain-list-community "$commit"
git -C "$tmp" -c advice.detachedHead=false checkout -q FETCH_HEAD
[ "$(git -C "$tmp" rev-parse HEAD)" = "$commit" ] || { echo "domain-list-community: не тот коммит" >&2; exit 1; }

mkdir -p "$dest"
# go на Windows путей Git Bash (/d/a/…) не понимает — ему путь Windows (D:/a/…).
out=$(cd "$dest" && { pwd -W 2> /dev/null || pwd; })
(cd "$tmp" && go run . --datapath=./data --outputname=geosite.dat --outputdir="$out")
[ -s "$dest/geosite.dat" ] || { echo "Генератор не создал $dest/geosite.dat" >&2; exit 1; }
echo "geosite.dat из v2fly/domain-list-community@${commit:0:12} → $dest"
