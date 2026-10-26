#!/bin/sh
# Пакет удаляется (а не обновляется): служба, если ее ставило окно («Установить службу»), удаляется вместе с юнитом
# systemd и блоком при обрыве — машина не остается без сети. Папка службы — /opt/private-gateway/service, ее не
# ставил пакет, поэтому и убирает ее служба — файлом пакета, а не своей копией: копия удаляла бы папку, из которой
# работает, и падала бы на первой еще не загруженной библиотеке. Обновление службу не трогает.
set -e
case "$1" in
  remove | purge | 0) ;;
  *) exit 0 ;;
esac

service=/opt/private-gateway/PrivateGateway.Service
if [ -x /opt/private-gateway/service/PrivateGateway.Service ]; then
  "$service" --uninstall || echo "Private Gateway: службу удалить не удалось — $service --uninstall" >&2
fi
