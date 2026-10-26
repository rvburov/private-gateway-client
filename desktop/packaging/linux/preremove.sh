#!/bin/sh
# Пакет удаляется (а не обновляется): служба, если ее ставило окно («Установить службу»), удаляется вместе с юнитом
# systemd и блоком при обрыве — машина не остается без сети. Папка службы — /opt/private-gateway/service, ее не
# ставил пакет, поэтому и убирает ее сама служба. Обновление службу не трогает.
set -e
case "$1" in
  remove | purge | 0) ;;
  *) exit 0 ;;
esac

service=/opt/private-gateway/service/PrivateGateway.Service
if [ -x "$service" ]; then
  "$service" --uninstall || echo "Private Gateway: службу удалить не удалось — $service --uninstall" >&2
fi
