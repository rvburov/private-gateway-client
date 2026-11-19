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
# Тестовое приложение само останавливается, если прогон идет на 20 минут дольше заданного, и вывод — с тем, на чем
# встало, — попадает в журнал: 2026-11-10 долгий прогон macOS выпуска 0.1.5 после «Режим прокси: 30 мин» молчал
# 88 минут, а отмена задания сняла его без вывода.
limit="--timeout $((minutes + 20))m"

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

# macOS и Linux: режим прокси — под сторожем scripts/soak-watchdog.py (как диагностика macos-soak-debug.yml): вывод сразу в
# журнал, отметки этапов теста (PG_SOAK_PROGRESS), а если этапы молчат 3 минуты — стеки управляемых потоков (dotnet-stack из
# PG_SOAK_STACK_TOOL, если задан) и остановка всей группы процессов. 2026-11-15–18 macOS трижды молчал после «Режим прокси»
# до снятия задания: при отмене живы были тест, dotnet test и xray — --timeout не помог, тест висел в ожидании, которое
# отмену не слушает. Отчет CTRF должен подтвердить ровно один пройденный тест без пропусков. Файлы — artifacts/soak/proxy.
watched() {
    local dir="$PWD/artifacts/soak/$1"
    shift
    rm -rf "$dir"
    mkdir -p "$dir"
    local stack=()
    if [ -n "${PG_SOAK_STACK_TOOL:-}" ]; then
        stack=(--stack-tool "$PG_SOAK_STACK_TOOL")
    fi

    python3 scripts/soak-watchdog.py \
        --timeout-seconds $(((minutes + 20) * 60)) --stack-after-seconds $(((minutes + 15) * 60)) --idle-seconds 180 \
        --progress "$dir/progress.log" --output-dir "$dir" --require-passed-ctrf "$dir/results" ${stack[@]+"${stack[@]}"} \
        -- env PG_SOAK_PROGRESS="$dir/progress.log" PG_SOAK_MINUTES="$minutes" "$@" \
        --results-directory "$dir/results" --report-ctrf -- --filter-method '*.Soak_proxy_mode_survives_failures_in_a_loop'
}

echo "Режим прокси: $minutes мин"
case "$(uname -s)" in
    Darwin | Linux)
        watched proxy dotnet test --project desktop/tests/Network -c Release --no-build $limit --output Detailed
        ;;
    *)
        run env PG_SOAK_MINUTES="$minutes" dotnet test --project desktop/tests/Network -c Release --no-build $limit \
            --output Detailed -- --filter-method '*.Soak_proxy_mode_survives_failures_in_a_loop'
        ;;
esac

if [ "$(uname -s)" = Linux ]; then
    echo "Режим VPN: $minutes мин (root)"
    sudo_prefix=()
    if [ "$(id -u)" != 0 ]; then
        sudo_prefix=(sudo env "PATH=$PATH" "DOTNET_ROOT=${DOTNET_ROOT:-}" "HOME=$HOME")
    fi
    run "${sudo_prefix[@]}" env PG_SOAK_MINUTES="$minutes" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
        dotnet test --project desktop/tests/Network -c Release --no-build $limit \
        --output Detailed -- --filter-method '*.Soak_vpn_survives_failures_in_a_loop_without_traffic_around_the_tunnel'
fi

case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*)
        echo "Режим VPN на Windows: $minutes мин (администратор)"
        run env PG_SOAK_MINUTES="$minutes" dotnet test --project desktop/tests/Network -c Release --no-build $limit \
            --output Detailed -- --filter-method '*.Soak_vpn_on_windows_survives_failures_in_a_loop_without_traffic_around_the_tunnel'
        ;;
esac
