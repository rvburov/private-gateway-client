#!/bin/sh
# Меню и значки рабочего стола узнают о новой записи сразу, а не после входа в систему.
command -v update-desktop-database > /dev/null && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache > /dev/null && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true

# Обновление пакета: служба, которую ставило окно, обновляется вместе с ним — для того же владельца, что в юните.
# Иначе новое окно говорило бы со старой службой (и старыми ядрами) до ее переустановки.
unit=/etc/systemd/system/private-gateway.service
if [ -f "$unit" ]; then
  owner=$(sed -n 's/.*"--owner" "\([0-9][0-9]*\)".*/\1/p' "$unit" | head -n 1)
  if [ -n "$owner" ]; then
    /opt/private-gateway/PrivateGateway.Service --install --owner "$owner" \
      || echo "Private Gateway: службу обновить не удалось — переустановите ее из окна («Установить службу»)" >&2
  fi
fi
exit 0
