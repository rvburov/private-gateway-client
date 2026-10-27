#!/usr/bin/env bash
# Долгий прогон (soak): сбои по кругу, пока не выйдет время, а сторож каждые 250 мс проверяет, не ушел ли трафик мимо
# туннеля, и не растут ли ресурсы службы. Режим прокси — на любой ОС; режим VPN — Linux от root (свое сетевое
# пространство, настоящие nftables, sing-box и Xray; машину правила не трогают) и Windows от администратора (настоящие
# WFP и TUN, но в туннель — только адрес теста, а блок — только для curl.exe сторожа: машина остается в сети). Ядра —
# scripts/get-xray.sh и scripts/get-singbox.sh. Сборка — Release.
#
#   scripts/soak.sh [минут на каждый режим, по умолчанию 30]
#
# В обычный прогон тестов soak не входит: без PG_SOAK_MINUTES он пропускается. Ночью его гоняет workflow soak.yml.
set -euo pipefail

cd "$(dirname "$0")/.."
minutes="${1:-30}"

# Пропуск здесь — ошибка: время задано, долгий прогон обязан пройти.
run() {
    local output
    output="$("$@" 2>&1)" || {
        echo "$output"
        exit 1
    }
    echo "$output" | grep -E 'Долгий прогон|total:|failed:|succeeded:|skipped:'
    if echo "$output" | grep -Eq 'skipped: [1-9]'; then
        echo "Долгий прогон пропущен — нет ядер или прав (см. вывод выше)" >&2
        exit 1
    fi
}

echo "Режим прокси: $minutes мин"
run env PG_SOAK_MINUTES="$minutes" dotnet test --project desktop/tests/Network -c Release --no-build \
    --output Detailed -- --filter-method '*.Soak_proxy_mode_survives_failures_in_a_loop'

if [ "$(uname -s)" = Linux ]; then
    echo "Режим VPN: $minutes мин (root)"
    sudo_prefix=()
    if [ "$(id -u)" != 0 ]; then
        sudo_prefix=(sudo env "PATH=$PATH" "DOTNET_ROOT=${DOTNET_ROOT:-}" "HOME=$HOME")
    fi
    run "${sudo_prefix[@]}" env PG_SOAK_MINUTES="$minutes" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
        dotnet test --project desktop/tests/Network -c Release --no-build \
        --output Detailed -- --filter-method '*.Soak_vpn_survives_failures_in_a_loop_without_traffic_around_the_tunnel'
fi

case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*)
        echo "Режим VPN на Windows: $minutes мин (администратор)"
        run env PG_SOAK_MINUTES="$minutes" dotnet test --project desktop/tests/Network -c Release --no-build \
            --output Detailed -- --filter-method '*.Soak_vpn_on_windows_survives_failures_in_a_loop_without_traffic_around_the_tunnel'
        ;;
esac
