#!/usr/bin/env bash
# Отказ удаления службы не позволяет удалить пакет. Все файлы и команды — в своей временной папке, без прав root.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/app/service"
touch "$tmp/app/service/PrivateGateway.Service"
chmod +x "$tmp/app/service/PrivateGateway.Service"
# Меняется только корень путей: выполняется настоящий prerm, с тем же управлением ошибками.
sed -e "s|/opt/private-gateway|$tmp/app|g" -e "s|/etc/systemd/system|$tmp/units|g" \
  "$root/desktop/packaging/linux/preremove.sh" > "$tmp/preremove.sh"
cat > "$tmp/app/PrivateGateway.Service" <<'SH'
#!/bin/sh
echo called >> "$(dirname "$0")/calls"
exit 23
SH
chmod +x "$tmp/app/PrivateGateway.Service"
for reason in remove purge 0; do
  if sh "$tmp/preremove.sh" "$reason"; then
    echo "НЕ ТАК: пакет удаляется после отказа службы ($reason)" >&2
    exit 1
  fi
done
[ "$(wc -l < "$tmp/app/calls")" -eq 3 ]
for reason in upgrade 1; do sh "$tmp/preremove.sh" "$reason"; done
[ "$(wc -l < "$tmp/app/calls")" -eq 3 ] || { echo "Обновление удаляет службу" >&2; exit 1; }
sed -i 's/exit 23/exit 0/' "$tmp/app/PrivateGateway.Service"
sh "$tmp/preremove.sh" remove
rm "$tmp/app/service/PrivateGateway.Service"
# Папка поврежденной службы осталась — удалить службу все равно необходимо.
sh "$tmp/preremove.sh" remove
[ "$(wc -l < "$tmp/app/calls")" -eq 5 ]
rmdir "$tmp/app/service"
sh "$tmp/preremove.sh" remove
[ "$(wc -l < "$tmp/app/calls")" -eq 5 ]
mkdir "$tmp/units"
touch "$tmp/units/private-gateway.service"
sh "$tmp/preremove.sh" remove
[ "$(wc -l < "$tmp/app/calls")" -eq 6 ]
echo "Удаление пакета останавливается на отказе службы; обновление и отсутствие службы обработаны"
