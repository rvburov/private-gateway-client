#!/bin/bash
#
# Проверка scripts/test-report.cs на собранных здесь отчетах CTRF: разрешенный пропуск проходит, неразрешенный и
# разрешенный только для другой ОС роняют, тест, пропущенный в одном запуске и прошедший в другом, считается
# прошедшим, упавший роняет, пустая папка роняет. Запускается в CI (задание «Стиль и формат») и вручную:
#
#   scripts/tests/test-report-smoke.sh
#
# Настоящих отчетов не трогает: все в mktemp -d, папка удаляется в конце.
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
cd "$ROOT"
unset GITHUB_STEP_SUMMARY

# Причина, разрешенная на всех ОС, и причина, разрешенная только не на этой.
ANY='долгий прогон — по PG_SOAK_MINUTES=<минут> (scripts/soak.sh)'
case "$(uname -s)" in
  Linux*|Darwin*) ELSEWHERE='SIGTERM — на macOS и Linux' ;;
  *) ELSEWHERE='реестр есть только на Windows' ;;
esac

# report <папка> <имя файла> <тест|статус|причина>…: отчет CTRF одного запуска.
report() {
  local dir="$TMP/$1" file="$2" tests="" name status reason
  shift 2
  mkdir -p "$dir"
  for t in "$@"; do
    IFS='|' read -r name status reason <<< "$t"
    tests+="${tests:+,}{\"name\":\"$name\",\"status\":\"$status\",\"suite\":\"s\",\"extra\":{\"reason\":\"$reason\"}}"
  done
  printf '{"results":{"extra":{"suites":[{"id":"s","filePath":"/x/PrivateGateway.Client.Tests.Unit.dll"}]},"tests":[%s]}}' "$tests" > "$dir/$file.ctrf"
}

fails=0

# expect <код выхода> <строка в выводе> <папка>
expect() {
  local code=0 out
  out=$(dotnet run scripts/test-report.cs "$TMP/$3" 2>&1) || code=$?
  if [ "$code" != "$1" ] || ! grep -qF -- "$2" <<< "$out"; then
    echo "НЕ ТАК: $3 — ждали код $1 и «$2», получили код $code:"
    echo "$out"
    fails=$((fails + 1))
  else
    echo "ок: $3"
  fi
}

report allowed a "T.Passes|passed|" "T.Soak|skipped|$ANY"
expect 0 "| **Итого** | **2** | **1** | **0** | **1** |" allowed

report forbidden a "T.Passes|passed|" "T.Proxy|skipped|нет сборки Xray — scripts/get-xray.sh"
expect 1 "**не разрешено на" forbidden

report elsewhere a "T.Other|skipped|$ELSEWHERE"
expect 1 "**не разрешено на" elsewhere

report merged user "T.Netns|skipped|нужен root: сетевое пространство" "T.Plain|passed|"
report merged root "T.Netns|passed|"
expect 0 "| **Итого** | **2** | **2** | **0** | **0** |" merged

report failed a "T.Broken|failed|" "T.Passes|passed|"
report failed b "T.Broken|passed|"
expect 1 "### Упало — 1" failed

mkdir -p "$TMP/empty"
expect 1 "нет отчетов CTRF" empty

if [ "$fails" -gt 0 ]; then
  echo "Проверок не прошло: $fails"
  exit 1
fi
echo "Все проверки отчета прошли"
