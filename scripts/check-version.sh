#!/bin/bash
#
# Сверка номера версии там, где он записан (по образцу SmartBase, Scripts/github-release/check-version.sh):
#
#   Directory.Build.props   <Version>0.1.5</Version>   ← источник правды: десктоп, Android и iOS берут номер отсюда
#   CHANGELOG.md            ## [0.1.5] — 2026-11-10    ← секция с описанием версии для страницы выпуска
#
# Использование:
#
#   scripts/check-version.sh                      # история в порядке, а у версии из Directory.Build.props, если ее секция
#                                                 # уже есть, она непуста (номер отладочных сборок может обгонять выпуск)
#   scripts/check-version.sh 0.1.5                # версия ровно 0.1.5 и ее секция в CHANGELOG.md непуста — так
#                                                 # проверяют release.sh после правки и выпуск на GitHub по тегу
#   scripts/check-version.sh --changelog-only 0.1.5
#                                                 # только секция в CHANGELOG.md (предусловие выпуска: секция пишется
#                                                 # ДО правки версии)
#
# Один файл на все точки вызова — release.sh, задание «Условия выпуска» в .github/workflows/release.yml и
# scripts/tests/release-test.sh: правила, разложенные по трем местам, разъезжаются.
#
# Код возврата: 0 — все сходится, 1 — есть расхождения (перечисляются все, а не только первое).
set -uo pipefail   # без -e: проверки должны выполниться все, а не оборваться на первой

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROPS="${CHECK_VERSION_PROPS:-$ROOT/Directory.Build.props}"
HISTORY="${CHECK_VERSION_CHANGELOG:-$ROOT/CHANGELOG.md}"

fail=0
err() { echo "  ✗ $1" >&2; fail=1; }

props_version() {
  sed -nE 's#.*<Version>([^<]*)</Version>.*#\1#p' "$PROPS" | head -n1
}

# Есть ли секция «## [версия]» (с датой или без) — 0, нет — 1.
changelog_section() {
  awk -v head="## [$1]" 'index($0, head) == 1 { found = 1; exit } END { exit !found }' "$HISTORY"
}

# Непуста ли секция «## [версия]»: между ее заголовком и следующим «## [» есть строка с текстом, не считая ссылок-сносок
# внизу файла («[0.1.5]: https://…»). Пустая секция значит, что версию завели, а описать забыли: страница выпуска без
# «Что нового» никому не нужна. Заголовок сравнивается как строка, а не регулярное выражение: «1.0.4» не совпадет с
# «1x0y4», а gawk не предупреждает об «\.» в -v.
changelog_section_filled() {
  awk -v head="## [$1]" '
    index($0, head) == 1   { found = 1; next }
    found && /^## \[/      { exit }
    found && /^\[[^]]*\]:/ { next }
    found && NF            { filled = 1; exit }
    END                    { exit !(found && filled) }
  ' "$HISTORY"
}

# ── Режим «только CHANGELOG»: предусловие выпуска ────────────────────────────
if [ "${1:-}" = "--changelog-only" ]; then
  want="${2:-}"
  [ -n "$want" ] || { echo "Использование: check-version.sh --changelog-only <версия>" >&2; exit 1; }
  [ -f "$HISTORY" ] || { err "нет файла CHANGELOG.md"; exit 1; }
  changelog_section_filled "$want" \
    || err "в CHANGELOG.md нет непустой секции «## [$want]» — опишите, что изменилось"
  exit $fail
fi

for f in "$PROPS" "$HISTORY"; do
  [ -f "$f" ] || { echo "Нет файла: $f" >&2; exit 1; }
done

PROPS_VER="$(props_version)"
[ -n "$PROPS_VER" ] || err "не удалось прочитать <Version> из Directory.Build.props"

# Общий порядок истории: одна секция «Не выпущено» и ни одной повторенной версии.
[ "$(grep -c '^## \[Не выпущено\]' "$HISTORY")" = 1 ] || err "CHANGELOG.md: должна быть ровно одна секция «## [Не выпущено]»"
dups="$(sed -nE 's/^## \[([0-9][^]]*)\].*/\1/p' "$HISTORY" | sort | uniq -d | tr '\n' ' ')"
[ -z "$dups" ] || err "CHANGELOG.md: версии описаны дважды: $dups"

if [ $# -gt 0 ]; then
  WANT="$1"
  # Бета 1.2.3-beta.1 (тег вручную, release.sh их не выпускает) идет на коде своей версии 1.2.3.
  [ "$PROPS_VER" = "$WANT" ] || [ "$PROPS_VER" = "${WANT%%-*}" ] || err "Directory.Build.props: $PROPS_VER, ожидалось $WANT"
  changelog_section_filled "$WANT" || err "CHANGELOG.md: нет непустой секции «## [$WANT]»"
elif [ -n "$PROPS_VER" ] && changelog_section "$PROPS_VER"; then
  WANT="$PROPS_VER"
  changelog_section_filled "$WANT" || err "CHANGELOG.md: секция «## [$WANT]» пустая"
else
  WANT="$PROPS_VER"
fi

if [ $fail -eq 0 ]; then
  echo "✓ версия $WANT согласована: Directory.Build.props, CHANGELOG.md"
else
  echo "Версии разошлись. Источник правды — <Version> в Directory.Build.props." >&2
fi
exit $fail
