#!/usr/bin/env bash
#
# Подготовка облачной машины агента к сборке и ко всем тестам Linux, как в CI: пакеты ОС и ядра.
# Один раз за сессию:  scripts/cloud-setup.sh
#
# Хук SessionStart (cloud-session.sh) его не запускает, а только напоминает: установка идет минуты,
# а хук держит начало сессии. На компьютере пользователя скрипт ничего не делает — там пакеты ставит
# он сам (docs/development.md).
set -euo pipefail

if [[ "${CLAUDE_CODE_REMOTE:-}" != true ]]; then
  echo "Скрипт — для облачной копии (CLAUDE_CODE_REMOTE=true); здесь пакеты ставятся по docs/development.md." >&2
  exit 1
fi
cd "$(dirname "${BASH_SOURCE[0]}")/.."

sudo=()
[[ $EUID -eq 0 ]] || sudo=(sudo)

# В свежей машине списки пакетов пусты: без update apt не находит ни одного.
#   dotnet-sdk-10.0 — сборка; загрузка с сайта Microsoft бывает закрыта сетью среды;
#   iproute2 — ip и ss: без них тесты Network на блок, маршруты и соединения падают, а не пропускаются;
#   nftables — блок при обрыве; gitleaks — проверка секретов перед отправкой в master;
#   gnome-keyring, libsecret-1-0, dbus — Secret Service для тестов хранилища (как в CI, без рекомендуемых).
"${sudo[@]}" apt-get update
"${sudo[@]}" apt-get install -y dotnet-sdk-10.0 iproute2 nftables gitleaks
"${sudo[@]}" apt-get install -y --no-install-recommends gnome-keyring libsecret-1-0 dbus

# Ядра для тестов Network. Выпуски GitHub бывают закрыты сетью среды — тогда Xray собирается
# из исходников тега (нужен go). sing-box из исходников не собирается: без выпусков его тесты пропускаются.
scripts/get-xray.sh || scripts/get-xray.sh --build
scripts/get-singbox.sh || echo "sing-box не скачан: тесты туннеля и relay будут пропущены (выпуски GitHub закрыты?)." >&2

cat << 'EOF'

Машина готова. Тесты хранилища секретов — в своей сессии D-Bus:
  dbus-run-session -- scripts/tests/secret-service.sh
В облаке ожидаемы пропуски, которых нет в CI: «в ядре машины нет IPv6» и «тест запущен от root» (AGENTS.md).
EOF
