#!/usr/bin/env bash
# Настоящая установка/удаление RPM с зависимостями в Fedora. Службу и брандмауэр контейнер не запускает:
# systemd, TUN и восстановление сети проверяются отдельно на машине .deb. Образ закреплен по manifest digest.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
version=${1:?версия выпуска}
image=fedora@sha256:99e203b80b1c3d8f7e161ec10a68fd02b081ef83a3963553e513c82846b97814 # Fedora 42, 2026-10-29
docker run --rm --volume "$PWD/artifacts/release:/packages:ro" --env "VERSION=$version" "$image" bash -euo pipefail -c '
  dnf install -y /packages/private-gateway-*.x86_64.rpm
  for file in /opt/private-gateway/PrivateGateway /opt/private-gateway/PrivateGateway.Service \
      /opt/private-gateway/cores/xray/xray /opt/private-gateway/cores/sing-box/sing-box \
      /usr/share/applications/private-gateway.desktop; do
    test -f "$file"
  done
  test "$(/opt/private-gateway/PrivateGateway.Service --version)" = "Private Gateway Service $VERSION (IPC v1)"
  test "$(readlink /usr/bin/private-gateway)" = /opt/private-gateway/PrivateGateway
  rpm -e private-gateway
  ! rpm -q private-gateway
  test ! -e /opt/private-gateway
  test ! -e /usr/bin/private-gateway
  test ! -e /usr/share/applications/private-gateway.desktop
  echo "RPM установлен с зависимостями и удален; служба systemd здесь не проверяется"
'
