#!/bin/bash
#
# Образ диска macOS на машине (CI, macOS, sudo без пароля): суммы сходятся, образ монтируется, в нем Private Gateway.app и
# ярлык на «Программы»; подпись пакета цела, в Info.plist — версия и исполняемый файл, значок на месте; окно, служба и ядра
# — внутри пакета. Служба ставится из пакета так, как ее ставит окно (от root, с владельцем — пользователем окна),
# работает под launchd, отвечает на сокете IPC, ее копия — без файлов окна; удаление убирает службу, ее описание и папки,
# а пакет после всего этого остается с целой подписью (служба в него не пишет). На компьютере пользователя не запускать —
# ставит службу:
#
#   scripts/tests/macos-package-smoke.sh <версия>
set -euo pipefail

version=${1:?версия выпуска}
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
case "$(uname -m)" in
  arm64) rid=osx-arm64 ;;
  *) rid=osx-x64 ;;
esac
dmg="artifacts/release/PrivateGateway-$version-$rid.dmg"
label=io.github.rvburov.private-gateway
plist="/Library/LaunchDaemons/$label.plist"
service="/Library/Application Support/Private Gateway/Service"
state="/Library/Application Support/Private Gateway/ServiceState"
mnt=$(mktemp -d)
apps=$(mktemp -d)
trap 'hdiutil detach "$mnt" -quiet 2> /dev/null || true; rm -rf "$apps"' EXIT

fail() {
  echo "НЕ ТАК: $*" >&2
  exit 1
}

[ -f "$dmg" ] || fail "нет $dmg"
(cd artifacts/release && shasum -a 256 -c SHA256SUMS) || fail "суммы SHA256SUMS"

hdiutil attach -nobrowse -readonly -mountpoint "$mnt" "$dmg" > /dev/null
[ -d "$mnt/Private Gateway.app" ] || fail "в образе нет Private Gateway.app"
[ "$(readlink "$mnt/Applications")" = /Applications ] || fail "в образе нет ярлыка на «Программы»"
codesign --verify --deep --strict "$mnt/Private Gateway.app" || fail "подпись пакета в образе не цела"
ditto "$mnt/Private Gateway.app" "$apps/Private Gateway.app"
hdiutil detach "$mnt" -quiet

app="$apps/Private Gateway.app"
codesign --verify --deep --strict "$app" || fail "подпись пакета после копирования не цела"
[ "$(plutil -extract CFBundleShortVersionString raw "$app/Contents/Info.plist")" = "${version%%-*}" ] || fail "версия в Info.plist"
[ "$(plutil -extract CFBundleExecutable raw "$app/Contents/Info.plist")" = PrivateGateway ] || fail "исполняемый файл в Info.plist"
[ -f "$app/Contents/Resources/PrivateGateway.icns" ] || fail "нет значка"
for file in PrivateGateway PrivateGateway.Service cores/xray/xray cores/sing-box/sing-box; do
  [ -x "$app/Contents/MacOS/$file" ] || fail "в пакете нет $file или он без права на запуск"
done
for file in cores/xray/geosite.dat THIRD-PARTY-NOTICES.txt sbom.cdx.json; do
  [ -f "$app/Contents/MacOS/$file" ] || fail "в пакете нет $file"
done
[ "$("$app/Contents/MacOS/PrivateGateway.Service" --version)" = "Private Gateway Service $version (IPC v1)" ] || fail "служба в пакете — не $version"

# Как окно (ServiceInstaller): служба из пакета, от root, владелец — пользователь окна.
sudo "$app/Contents/MacOS/PrivateGateway.Service" --install --owner "$(id -u)"
[ -f "$plist" ] || fail "нет $plist"
sudo launchctl print "system/$label" | grep -q 'state = running' || fail "служба не работает: $(sudo launchctl print "system/$label" 2>&1 | head -20)"
[ "$("$service/PrivateGateway.Service" --version)" = "Private Gateway Service $version (IPC v1)" ] || fail "поставлена служба не $version"
[ -x "$service/cores/xray/xray" ] && [ -x "$service/cores/sing-box/sing-box" ] || fail "ядра не скопированы к службе"
[ ! -e "$service/PrivateGateway" ] && [ ! -e "$service/libAvaloniaNative.dylib" ] || fail "служба возит с собой окно"
for _ in $(seq 1 50); do [ -S /var/run/private-gateway/ipc.sock ] && break; sleep 0.2; done
[ -S /var/run/private-gateway/ipc.sock ] || fail "служба не слушает /var/run/private-gateway/ipc.sock"
[ "$(sudo stat -f %Lp "$state")" = 700 ] || fail "папка состояния не только для root"

sudo "$app/Contents/MacOS/PrivateGateway.Service" --uninstall
[ ! -e "$plist" ] || fail "после удаления остался $plist"
! sudo launchctl print "system/$label" > /dev/null 2>&1 || fail "после удаления служба загружена в launchd"
[ ! -e "$service" ] || fail "после удаления осталась папка службы"
[ ! -e "$state/journal.jsonl" ] || fail "после удаления остался журнал службы"
codesign --verify --deep --strict "$app" || fail "служба изменила файлы пакета"
echo "Пакет macOS $version: образ, подпись, установка службы из пакета и удаление — в порядке"
