#!/bin/bash
#
# Установка и удаление пакета .deb на машине (CI, Ubuntu, от root через sudo): пакет ставится со всеми зависимостями,
# окно и служба на месте, запись меню проходит desktop-file-validate, служба из пакета ставится так же, как ее ставит
# окно (без файлов окна, состояние — в /var/lib/private-gateway), и работает под systemd; копия службы, которая не
# запускается, сменяется прежней; обновление пакета переставляет службу тем же владельцем; удаление пакета без ошибок
# убирает службу, юнит, таблицу блока при обрыве, ее состояние и все файлы пакета, а данные пользователя не трогает. На
# компьютере пользователя не запускать — ставит службу:
#
#   scripts/tests/linux-package-smoke.sh <версия>
set -euo pipefail

version=${1:?версия выпуска}
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
deb=$(ls artifacts/release/private-gateway_*_amd64.deb)

fail() {
  echo "НЕ ТАК: $*" >&2
  # Что было со службой — для разбора в журнале CI.
  sudo systemctl status private-gateway --no-pager 2>&1 | tail -20 >&2 || true
  sudo journalctl -u private-gateway --no-pager -n 60 2>&1 >&2 || true
  sudo ls -la /var/lib/private-gateway /opt/private-gateway/service 2>&1 | head -40 >&2 || true
  exit 1
}

tmp_out=$(mktemp)
trap 'rm -f "$tmp_out"' EXIT

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
sudo /opt/private-gateway/PrivateGateway.Service --install --owner "$(id -u)" || fail "служба не установилась"
for _ in $(seq 1 50); do
  systemctl is-active --quiet private-gateway && break
  sleep 0.2
done
systemctl is-active --quiet private-gateway || fail "служба не запустилась под systemd"
[ ! -e /opt/private-gateway/service/PrivateGateway ] && [ ! -e /opt/private-gateway/service/libSkiaSharp.so ] || fail "служба возит с собой окно"
[ "$(sudo stat -c %a /var/lib/private-gateway)" = 700 ] || fail "папка состояния службы открыта не только root"
sudo test -f /var/lib/private-gateway/journal.jsonl || fail "служба не пишет журнал в /var/lib/private-gateway"
grep -q '"--state" "/var/lib/private-gateway"' /etc/systemd/system/private-gateway.service || fail "у службы нет папки состояния"

# Новая копия службы не запускается (нет нужной библиотеки) — установка возвращает прежнюю и отвечает ошибкой.
broken=$(mktemp -d)
sudo cp -a /opt/private-gateway/. "$broken/"
sudo rm -rf "$broken/service" "$broken/Grpc.AspNetCore.Server.dll"
if sudo "$broken/PrivateGateway.Service" --install --owner "$(id -u)"; then
  fail "установка службы, которая не запускается, прошла"
fi
sudo rm -rf "$broken"
systemctl is-active --quiet private-gateway || fail "после неудачной замены служба не работает"
[ -f /opt/private-gateway/service/Grpc.AspNetCore.Server.dll ] || fail "после неудачной замены на месте не прежняя копия"
[ ! -e /opt/private-gateway/service.old ] && [ ! -e /opt/private-gateway/service.new ] || fail "после отката остались service.old/.new"

# Переустановка пакета — как обновление: прежняя служба не удаляется (prerm «upgrade»), а ставится заново из нового
# пакета тем же владельцем (postinst) и снова работает.
# Время изменения inode (ctime) не переносится копированием: новое — значит, файл службы записан заново.
before=$(stat -c %Z /opt/private-gateway/service/PrivateGateway.Service)
started=$(systemctl show -p ActiveEnterTimestampMonotonic --value private-gateway)
sleep 1.1
sudo dpkg -i "$deb" 2>&1 | tee "$tmp_out"
! grep -Eq "не удалось|Unhandled exception" "$tmp_out" || fail "обновление пакета: служба переставилась с ошибкой"
[ "$(stat -c %Z /opt/private-gateway/service/PrivateGateway.Service)" -gt "$before" ] || fail "после обновления пакета служба не переставлена"
[ "$(systemctl show -p ActiveEnterTimestampMonotonic --value private-gateway)" != "$started" ] || fail "после обновления служба не перезапущена"
grep -q "\"--owner\" \"$(id -u)\"" /etc/systemd/system/private-gateway.service || fail "после обновления у службы другой владелец"
for _ in $(seq 1 50); do
  systemctl is-active --quiet private-gateway && break
  sleep 0.2
done
systemctl is-active --quiet private-gateway || fail "после обновления пакета служба не работает"

# Отказ CLI удаления: менеджер пакетов обязан остановиться ДО удаления файлов окна и службы.
sudo mv /opt/private-gateway/PrivateGateway.Service /opt/private-gateway/PrivateGateway.Service.hidden
if sudo apt-get remove -y private-gateway > "$tmp_out" 2>&1; then
  sudo mv /opt/private-gateway/PrivateGateway.Service.hidden /opt/private-gateway/PrivateGateway.Service
  fail "удаление пакета прошло, хотя служба не могла быть удалена"
fi
sudo mv /opt/private-gateway/PrivateGateway.Service.hidden /opt/private-gateway/PrivateGateway.Service
[ -f /opt/private-gateway/PrivateGateway ] || fail "отказ удаления убрал окно"
[ -f /etc/systemd/system/private-gateway.service ] || fail "отказ удаления потерял регистрацию службы"
systemctl is-active --quiet private-gateway || fail "отказ удаления остановил работающую службу"

# Удаление — без ошибок службы в выводе: ошибка означала бы оставшиеся правила или юнит.
sudo apt-get remove -y private-gateway 2>&1 | tee "$tmp_out"
! grep -Eq "не удалось|Unhandled exception" "$tmp_out" || fail "удаление пакета: служба удалилась с ошибкой"
! systemctl cat private-gateway > /dev/null 2>&1 || fail "после удаления пакета остался юнит службы"
[ ! -e /opt/private-gateway ] || fail "после удаления пакета осталась /opt/private-gateway: $(ls -A /opt/private-gateway)"
[ ! -e /var/lib/private-gateway ] || fail "после удаления пакета осталось состояние службы: $(sudo ls -A /var/lib/private-gateway)"
[ ! -e /usr/bin/private-gateway ] || fail "осталась команда private-gateway"
! sudo nft list table inet private_gateway > /dev/null 2>&1 || fail "осталась таблица блока при обрыве"
[ "$(cat "$HOME/.local/share/private-gateway/marker")" = keep ] || fail "удаление тронуло данные пользователя"
echo "Пакет $deb ставится и удаляется начисто"
