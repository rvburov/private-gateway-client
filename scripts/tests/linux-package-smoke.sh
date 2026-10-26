#!/bin/bash
#
# Установка и удаление пакета .deb на машине (CI, Ubuntu, от root через sudo): пакет ставится со всеми зависимостями,
# окно и служба на месте, запись меню проходит desktop-file-validate, служба из пакета ставится так же, как ее ставит
# окно, и работает под systemd; удаление пакета убирает службу, юнит, таблицу блока при обрыве и все файлы пакета, а
# данные пользователя не трогает. На компьютере пользователя не запускать — ставит службу:
#
#   scripts/tests/linux-package-smoke.sh <версия>
set -euo pipefail

version=${1:?версия выпуска}
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
deb=$(ls artifacts/release/private-gateway_*_amd64.deb)

fail() {
  echo "НЕ ТАК: $*" >&2
  exit 1
}

# Данные пользователя (настройки окна) — до установки: удаление пакета не должно их тронуть.
mkdir -p "$HOME/.local/share/private-gateway"
echo keep > "$HOME/.local/share/private-gateway/marker"

sudo apt-get install -y --no-install-recommends "./$deb"
for file in /opt/private-gateway/PrivateGateway /opt/private-gateway/PrivateGateway.Service /opt/private-gateway/cores/xray/xray \
  /opt/private-gateway/cores/sing-box/sing-box /opt/private-gateway/THIRD-PARTY-NOTICES.txt /usr/share/applications/private-gateway.desktop \
  /usr/share/icons/hicolor/scalable/apps/private-gateway.svg; do
  [ -e "$file" ] || fail "после установки нет $file"
done
[ "$(readlink -f /usr/bin/private-gateway)" = /opt/private-gateway/PrivateGateway ] || fail "команда private-gateway не ведет к окну"
[ "$(/opt/private-gateway/PrivateGateway.Service --version)" = "Private Gateway Service $version (IPC v1)" ] || fail "служба из пакета — не $version"
desktop-file-validate /usr/share/applications/private-gateway.desktop || fail "запись меню не проходит desktop-file-validate"

# Служба — как ее ставит окно: от root, владелец — пользователь окна.
sudo /opt/private-gateway/PrivateGateway.Service --install --owner "$(id -u)"
for _ in $(seq 1 50); do
  systemctl is-active --quiet private-gateway && break
  sleep 0.2
done
systemctl is-active --quiet private-gateway || fail "служба не запустилась под systemd"

sudo apt-get remove -y private-gateway
! systemctl cat private-gateway > /dev/null 2>&1 || fail "после удаления пакета остался юнит службы"
[ ! -e /opt/private-gateway ] || fail "после удаления пакета осталась /opt/private-gateway: $(ls -A /opt/private-gateway)"
[ ! -e /usr/bin/private-gateway ] || fail "осталась команда private-gateway"
! sudo nft list table inet private_gateway > /dev/null 2>&1 || fail "осталась таблица блока при обрыве"
[ "$(cat "$HOME/.local/share/private-gateway/marker")" = keep ] || fail "удаление тронуло данные пользователя"
echo "Пакет $deb ставится и удаляется начисто"
