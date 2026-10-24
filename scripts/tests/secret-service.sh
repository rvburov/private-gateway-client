#!/usr/bin/env bash
# Тесты хранилища секретов Linux на настоящем Secret Service: своя сессия D-Bus и GNOME Keyring
# с пустым паролем. Нужны пакеты gnome-keyring, libsecret-1-0 и dbus.
#
#   dbus-run-session -- scripts/tests/secret-service.sh [-c Release --no-build]
#
# Без запущенного Secret Service эти тесты пропускаются — скрипт для CI и проверки вручную.
set -euo pipefail

cd "$(dirname "$0")/../.."
eval "$(printf '\n' | gnome-keyring-daemon --unlock --components=secrets)"
export GNOME_KEYRING_CONTROL
printf '\n' | gnome-keyring-daemon --start --components=secrets >/dev/null

# Пропуск здесь — ошибка: хранилище запущено, тесты обязаны пройти.
output="$(dotnet test --project desktop/tests/Unit "$@" -- --filter-class "*SecretStoreTests" 2>&1)" || {
    echo "$output"
    exit 1
}
echo "$output" | grep -E 'total:|failed:|succeeded:|skipped:'
if echo "$output" | grep -Eq 'skipped: [1-9]'; then
    echo "Secret Service запущен, а тесты хранилища пропущены" >&2
    exit 1
fi
