#!/bin/bash
#
# Тесты выпуска по образцу SmartBase (Scripts/github-release/release-test.sh): scripts/release.sh, check-version.sh,
# release-notes.sh, триггеры .github/workflows/release.yml, кнопка «Выпустить версию» (release-button.yml) и отправка на
# сайт (шаги website.yml на поддельных gh и ssh, вызов из release-rollout.yml). Чистый bash и git (для сайта еще jq):
# .NET, сеть и gh не нужны. Запускается в CI (задание «Стиль и формат») и вручную:
#
#   scripts/tests/release-test.sh
#
# Главный инвариант: прогон НИЧЕГО не меняет в репозитории — release.sh гоняется на отдельном стенде с локальным origin
# (проверка 9 подтверждает это машинно).
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT" || exit 1

passed=0
failed=0
skipped=0

ok()   { echo "✅ $1"; passed=$((passed + 1)); }
bad()  { echo "❌ $1"; [ $# -gt 1 ] && printf '   %s\n' "${@:2}"; failed=$((failed + 1)); }
skip() { echo "⏭  $1"; skipped=$((skipped + 1)); }

STATUS_BEFORE="$(git status --porcelain)"
VER="$(sed -nE 's#.*<Version>([^<]*)</Version>.*#\1#p' Directory.Build.props | head -n1)"

# Стенд: свой репозиторий с локальным origin, своя версия 1.4.2 и прошлый выпуск v1.4.1. Ни сеть, ни ветка пользователя
# для проверки выпуска не нужны.
FIXTURE="$(mktemp -d "${TMPDIR:-/tmp}/pg-release-test.XXXXXX")" || exit 1
trap 'rm -rf "$FIXTURE"' EXIT
SRC="$FIXTURE/source"
fixture_git() { git -C "$SRC" "$@"; }
fixture_commit() { fixture_git commit -qm "$1"; }
fixture_setup() {
  mkdir -p "$SRC/scripts" "$FIXTURE/no-hooks" || return 1
  cp scripts/release.sh scripts/check-version.sh scripts/release-notes.sh "$SRC/scripts/" || return 1
  printf '<Project>\n  <PropertyGroup>\n    <Version>1.4.2</Version>\n  </PropertyGroup>\n</Project>\n' > "$SRC/Directory.Build.props"
  printf '# История выпусков\n\n## [Не выпущено]\n\n## [1.4.1] — 2026-11-01\n\n### Добавлено\n\n- Прошлая версия.\n\n[Не выпущено]: https://example.com/compare/v1.4.1...HEAD\n[1.4.1]: https://example.com/releases/tag/v1.4.1\n' \
    > "$SRC/CHANGELOG.md"
  git init --bare -q "$FIXTURE/origin.git" || return 1
  git init -q -b master "$SRC" || return 1
  fixture_git config core.hooksPath "$FIXTURE/no-hooks" && fixture_git config user.name 'Roman Burov' \
    && fixture_git config user.email 'romanburov89@gmail.com' && fixture_git config commit.gpgsign false \
    && fixture_git config tag.gpgsign false && fixture_git config push.negotiate false || return 1
  fixture_git add . && fixture_commit 'Проверочная версия' || return 1
  fixture_git tag -a v1.4.1 -m 'Прошлый выпуск' || return 1
  fixture_git remote add origin "$FIXTURE/origin.git" || return 1
  fixture_git push -q -u origin master --tags || return 1
}
fixture_state() {
  fixture_git status --porcelain
  fixture_git rev-parse HEAD
  fixture_git show-ref
  git -C "$FIXTURE/origin.git" show-ref
}
if ! fixture_setup; then
  bad 'Не удалось создать стенд выпуска'
  exit 1
fi

echo "Тесты выпуска (версия репозитория $VER)"
echo

# ── 1. Синтаксис скриптов ─────────────────────────────────────────────────────
errs=""
for s in scripts/release.sh scripts/check-version.sh scripts/release-notes.sh scripts/tests/release-test.sh; do
  bash -n "$s" 2>/dev/null || errs="$errs $s"
done
[ -z "$errs" ] && ok "1. Синтаксис скриптов выпуска" || bad "1. Синтаксис скриптов" "ошибки в:$errs"

# ── 2. check-version.sh ───────────────────────────────────────────────────────
if scripts/check-version.sh >/dev/null 2>&1; then
  out="$(scripts/check-version.sh 9.9.9 2>&1)"
  if [ $? -ne 0 ] && [ "$(printf '%s\n' "$out" | grep -c '✗')" -eq 2 ]; then
    ok "2. check-version.sh: репозиторий согласован, на 9.9.9 — два расхождения (Directory.Build.props и CHANGELOG.md)"
  else
    bad "2. check-version.sh не отчитался о двух расхождениях на 9.9.9" "$out"
  fi
else
  bad "2. check-version.sh падает на текущем состоянии" "$(scripts/check-version.sh 2>&1)"
fi

# ── 2а. Заголовок версии — строкой, а не регулярным выражением ────────────────
# Точка версии в регулярном выражении — любой знак («1.4.2» совпало бы с «1x4x2»), а «\.» в awk -v gawk (Git Bash на
# Windows) печатает предупреждением при каждом выпуске.
printf '# История\n\n## [Не выпущено]\n\n## [1x4x2]\n\n- Чужая секция.\n' > "$FIXTURE/dots.md"
prob=()
CHECK_VERSION_CHANGELOG="$FIXTURE/dots.md" scripts/check-version.sh --changelog-only 1.4.2 >/dev/null 2>&1 \
  && prob+=("check-version.sh принял «## [1x4x2]» за 1.4.2")
RELEASE_CHANGELOG="$FIXTURE/dots.md" scripts/release-notes.sh 1.4.2 >/dev/null 2>&1 \
  && prob+=("release-notes.sh принял «## [1x4x2]» за 1.4.2")
if command -v gawk >/dev/null 2>&1; then
  mkdir -p "$FIXTURE/gawk" && ln -sf "$(command -v gawk)" "$FIXTURE/gawk/awk"
  warn="$( { PATH="$FIXTURE/gawk:$PATH" scripts/check-version.sh "$VER" >/dev/null
             PATH="$FIXTURE/gawk:$PATH" scripts/release-notes.sh "$VER" >/dev/null; } 2>&1 )"
  [ -z "$warn" ] || prob+=("с gawk лишний вывод: $warn")
fi
if [ ${#prob[@]} -ne 0 ]; then
  bad "2а. Заголовок версии в CHANGELOG.md" "${prob[@]}"
elif command -v gawk >/dev/null 2>&1; then
  ok "2а. Заголовок версии сравнивается строкой: «1x4x2» — не 1.4.2, gawk без предупреждений"
else
  ok "2а. Заголовок версии сравнивается строкой: «1x4x2» — не 1.4.2"
  skip "2б. gawk не установлен — предупреждения gawk не проверены"
fi

# ── 3. release.sh отвергает плохие версии ─────────────────────────────────────
reject() {   # отказ должен быть именно из-за версии, а не ветки, сети или истории
  local result code
  result="$(bash "$SRC/scripts/release.sh" "$1" --dry-run --skip-tests 2>&1)"
  code=$?
  [ "$code" -ne 0 ] && printf '%s\n' "$result" | grep -F "$2" >/dev/null
}
bad3=""
reject "1.4" 'не в формате X.Y.Z'         || bad3="$bad3 «1.4» (не X.Y.Z)"
reject "1.4.3-beta.1" 'не в формате X.Y.Z' || bad3="$bad3 «1.4.3-beta.1» (суффикс)"
reject "1.4.3.1" 'не в формате X.Y.Z'      || bad3="$bad3 «1.4.3.1» (четыре числа)"
reject "1.4.0" 'меньше текущей'            || bad3="$bad3 «1.4.0» (меньше Directory.Build.props)"
reject "1.4.1" 'уже есть локально'         || bad3="$bad3 «1.4.1» (тег занят)"
[ -z "$bad3" ] && ok "3. release.sh отвергает неверные и занятые версии" || bad "3. release.sh пропустил:$bad3"

# Версия не меньше отладочной, но не больше прошлого выпуска: временный тег v1.5.0 выше отладочной 1.4.2. Выпуски
# release.sh берет с «GitHub», поэтому тег отправляется на origin стенда.
fixture_git tag v1.5.0 && fixture_git push -q origin v1.5.0 && {
  reject "1.4.2" 'не больше последнего выпуска' && ok "3а. release.sh: версия не больше прошлого выпуска — отказ" \
    || bad "3а. release.sh пропустил версию не больше прошлого выпуска v1.5.0"
  fixture_git push -q origin :refs/tags/v1.5.0
  fixture_git tag -d v1.5.0 >/dev/null
}

# ── 4. Счастливый путь --dry-run ──────────────────────────────────────────────
out="$(bash "$SRC/scripts/release.sh" 1.4.2 --dry-run --skip-tests 2>&1)"
code=$?
if [ "$code" -eq 0 ] || ! printf '%s\n' "$out" | grep -F 'CHANGELOG.md не готов' >/dev/null; then
  bad '4. Версия без секции в CHANGELOG.md должна останавливать выпуск' "$out"
else
  awk '{ print } /^## \[Не выпущено\]/ { print ""; print "## [1.4.2]"; print ""; print "### Исправлено"; print ""; print "- Изменение для проверки выпуска."; print "  Продолжение строки." }' \
    "$SRC/CHANGELOG.md" > "$FIXTURE/history" && mv "$FIXTURE/history" "$SRC/CHANGELOG.md"
  fixture_git add CHANGELOG.md
  if fixture_commit 'Описание версии' && fixture_git push -q origin master; then
    fixture_before="$(fixture_state)"
    out="$(bash "$SRC/scripts/release.sh" 1.4.2 --dry-run --skip-tests 2>&1)"
    code=$?
    if [ "$code" -ne 0 ]; then
      bad "4. release.sh --dry-run 1.4.2 завершился с ошибкой" "$out"
    elif ! printf '%s\n' "$out" | grep -F 'проверки пройдены' >/dev/null; then
      bad "4. release.sh --dry-run 1.4.2 не напечатал итог" "$out"
    elif [ "$(fixture_state)" != "$fixture_before" ]; then
      bad '4. --dry-run изменил файлы, коммит или ссылки стенда'
    else
      ok "4. Секция версии обязательна; --dry-run печатает план и ничего не меняет"
    fi
  else
    bad '4. Не удалось подготовить историю на стенде'
  fi
fi

# ── 5. Настоящий выпуск на стенде ─────────────────────────────────────────────
# Отказ на вопросе «Выпустить?» ничего не меняет; согласие — коммит, аннотированный тег, дата и ссылки, отправка.
before="$(fixture_state)"
out="$(echo n | bash "$SRC/scripts/release.sh" 1.4.2 --skip-tests 2>&1)"
if [ "$(fixture_state)" != "$before" ] || ! printf '%s\n' "$out" | grep -F 'Отменено' >/dev/null; then
  bad '5. Ответ «n» должен отменять выпуск без изменений' "$out"
else
  # Тег удаленного на GitHub выпуска остался только локально (git fetch --tags его не убирает) — прошлым выпуском он не
  # считается: иначе «1.4.2-beta.1» не пустил бы 1.4.2, а ссылка «сравнить» вела бы на тег, которого на GitHub нет.
  fixture_git tag v1.4.2-beta.1
  out="$(echo y | bash "$SRC/scripts/release.sh" 1.4.2 --skip-tests 2>&1)"
  code=$?
  fixture_git tag -d v1.4.2-beta.1 > /dev/null
  prob=()
  [ "$code" -eq 0 ] || prob+=("код $code: $out")
  printf '%s\n' "$out" | grep -qF 'прошлый выпуск — v1.4.1' || prob+=("прошлый выпуск — не v1.4.1 с origin: $out")
  [ "$(fixture_git log -1 --format=%s)" = "release: v1.4.2" ] || prob+=("последний коммит — не «release: v1.4.2»")
  [ "$(fixture_git cat-file -t v1.4.2 2>/dev/null)" = tag ] || prob+=("тег v1.4.2 не аннотированный или его нет")
  [ "$(git -C "$FIXTURE/origin.git" rev-parse master 2>/dev/null)" = "$(fixture_git rev-parse HEAD)" ] || prob+=("master не отправлен")
  git -C "$FIXTURE/origin.git" rev-parse -q --verify refs/tags/v1.4.2 >/dev/null || prob+=("тег не отправлен")
  grep -qE "^## \[1\.4\.2\] — [0-9]{4}-[0-9]{2}-[0-9]{2}$" "$SRC/CHANGELOG.md" || prob+=("у секции 1.4.2 нет даты")
  grep -qxF '[Не выпущено]: https://github.com/rvburov/private-gateway-client/compare/v1.4.2...HEAD' "$SRC/CHANGELOG.md" \
    || prob+=("ссылка «Не выпущено» не сдвинута")
  grep -qxF '[1.4.2]: https://github.com/rvburov/private-gateway-client/compare/v1.4.1...v1.4.2' "$SRC/CHANGELOG.md" \
    || prob+=("нет ссылки сравнения 1.4.1...1.4.2")
  [ "$(grep -c '^\[Не выпущено\]:' "$SRC/CHANGELOG.md")" = 1 ] || prob+=("ссылка «Не выпущено» задвоилась")
  [ -z "$(fixture_git status --porcelain)" ] || prob+=("после выпуска остались незакоммиченные файлы")
  if [ ${#prob[@]} -eq 0 ]; then
    ok "5. Выпуск: «n» — без изменений; «y» — коммит, аннотированный тег, дата и ссылки, master и тег отправлены; прошлый выпуск — с origin, а не с локального тега"
  else
    bad "5. Выпуск на стенде" "${prob[@]}"
  fi
fi

# ── 6. release-notes.sh на этой версии репозитория ────────────────────────────
notes="$(scripts/release-notes.sh "$VER" 2>&1)"
if [ $? -ne 0 ]; then
  bad "6. release-notes.sh $VER завершился с ошибкой" "$notes"
else
  miss=""
  for marker in "## Что нового" "## Установка" "### Первый запуск" "## Обновление" "## Сверка загрузки" \
                "PrivateGateway-$VER-win-x64.msi" "PrivateGateway-$VER-osx-arm64.dmg" "private-gateway_${VER}_amd64.deb" \
                "private-gateway-${VER}-1.x86_64.rpm" "PrivateGateway-$VER-android.apk" \
                "Выполнить в любом случае" "Все равно открыть"; do
    printf '%s\n' "$notes" | grep -F "$marker" > /dev/null || miss="$miss «${marker}»"
  done
  # Незаполненные заготовки: <версия>, TODO, XXX, {{…}}. Явный алфавит: диапазон а-я зависит от LC_COLLATE.
  cyrillic='абвгдежзийклмнопрстуфхцчшщъыьэюяАБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ'
  left="$(printf '%s\n' "$notes" | grep -nE "<[$cyrillic][$cyrillic ]*>|TODO|XXX|\\{\\{")"
  mentions="$(printf '%s\n' "$notes" | grep -nE '(^|[^`[:alnum:]_])@[[:alnum:]]')"
  if [ -n "$miss" ]; then
    bad "6. В тексте выпуска не хватает:$miss"
  elif [ -n "$left" ]; then
    bad "6. В тексте выпуска остались заготовки" "$left"
  elif [ -n "$mentions" ]; then
    bad "6. В тексте выпуска есть упоминания учетных записей" "$mentions"
  elif [ "$(printf '%s\n' "$notes" | wc -l)" -gt 70 ]; then
    bad "6. Текст выпуска длиннее 70 строк — это уже отчет о работе"
  else
    ok "6. release-notes.sh $VER: все разделы и файлы, без заготовок и упоминаний"
  fi
fi

# ── 7. release-notes.sh: правила текста ───────────────────────────────────────
cat > "$FIXTURE/CHANGELOG.md" <<'CHANGES'
# История выпусков

## [Не выпущено]

- В выпуск 2.0.0 не попадает.

## [2.0.0] — 2026-12-01

### Исправлено

- Предупреждение KT-73255 из-за @param:ApplicationContext больше не мешает сборке. Пишите на user@example.com, код
  `@уже` не трогается.

## [1.0.0]

- Старое.

[2.0.0]: https://example.com/compare/v1.0.0...v2.0.0
CHANGES
notes="$(RELEASE_CHANGELOG="$FIXTURE/CHANGELOG.md" scripts/release-notes.sh 2.0.0 2>&1)"
prob=()
printf '%s\n' "$notes" | grep -qF '`@param:ApplicationContext`' || prob+=("«@param:…» не стал кодом")
printf '%s\n' "$notes" | grep -qF 'user@example.com,' || prob+=("почта испорчена")
printf '%s\n' "$notes" | grep -qF '`@уже`' || prob+=("код в обратных кавычках испорчен")
printf '%s\n' "$notes" | grep -qxF -- '- Предупреждение KT-73255 из-за `@param:ApplicationContext` больше не мешает сборке. Пишите на user@example.com, код `@уже` не трогается.' \
  || prob+=("перенос строки внутри пункта не склеен")
printf '%s\n' "$notes" | grep -qE 'Старое|В выпуск 2.0.0|example.com/compare' && prob+=("в текст попали чужие секции или сноски")
printf '%s\n' "$notes" | grep -qE '^(Автор|Contributors)' && prob+=("в тексте строка автора")
RELEASE_CHANGELOG="$FIXTURE/CHANGELOG.md" scripts/release-notes.sh 3.0.0 >/dev/null 2>&1 && prob+=("версия без секции не остановила")
mkdir -p "$FIXTURE/out"
RELEASE_CHANGELOG="$FIXTURE/CHANGELOG.md" scripts/release-notes.sh 2.0.0 "$FIXTURE/out" >/dev/null 2>&1 \
  && prob+=("пустая папка выпуска не остановила")
for f in PrivateGateway-2.0.0-win-x64.msi PrivateGateway-2.0.0-osx-arm64.dmg private-gateway_2.0.0_amd64.deb \
         private-gateway-2.0.0-1.x86_64.rpm PrivateGateway-2.0.0-android.apk; do : > "$FIXTURE/out/$f"; done
RELEASE_CHANGELOG="$FIXTURE/CHANGELOG.md" scripts/release-notes.sh 2.0.0 "$FIXTURE/out" >/dev/null 2>&1 \
  || prob+=("папка со всеми файлами не прошла")
if [ ${#prob[@]} -eq 0 ]; then
  ok "7. Текст выпуска: «@слово» — кодом, почта цела, строки склеены, только своя секция, без автора; нет секции или файла — отказ"
else
  bad "7. Правила текста выпуска" "${prob[@]}"
fi

# ── 8. Workflow выпуска ───────────────────────────────────────────────────────
WF=".github/workflows/release.yml"
wf_problems=""
grep -q "tags: \['v\*'\]" "$WF"            || wf_problems="$wf_problems нет триггера по тегам v*;"
grep -qE "^\s+branches:" "$WF"             && wf_problems="$wf_problems ЛИШНИЙ триггер по веткам;"
grep -q "pull_request:" "$WF"              && wf_problems="$wf_problems ЛИШНИЙ триггер pull_request;"
grep -q "scripts/release-notes.sh" "$WF"   || wf_problems="$wf_problems текст выпуска не из release-notes.sh;"
grep -q "scripts/check-version.sh" "$WF"   || wf_problems="$wf_problems версия тега не сверяется;"
grep -q "uses: ./.github/workflows/soak.yml" "$WF" || wf_problems="$wf_problems нет долгого прогона коммита тега;"
grep -q "gh release edit" "$WF"            || wf_problems="$wf_problems повтор запуска не обновляет готовый выпуск;"
grep -qF '"+refs/tags/$RELEASE_TAG:refs/tags/$RELEASE_TAG"' "$WF" \
  || wf_problems="$wf_problems тег перед подписью качается без «+» — аннотированный тег не встанет на место простого;"
# В шагах с «-o pipefail» «команда | grep -q …» ломается: grep закрывает канал на первом совпадении, источник получает
# SIGPIPE, и шаг падает именно потому, что совпадение нашлось (так в SmartBase сломалась сборка linux-x64).
if grep -nE '^[[:space:]]*[^#[:space:]].*\|[[:space:]]*grep[[:space:]]+-[A-Za-z]*q' "$WF" > /dev/null; then
  wf_problems="$wf_problems «| grep -q» ломается о pipefail — уберите -q;"
fi
if [ -n "$wf_problems" ]; then
  bad "8. Проблемы в $WF" "$wf_problems"
elif command -v actionlint >/dev/null 2>&1; then
  out="$(actionlint "$WF" .github/workflows/soak.yml .github/workflows/release-button.yml .github/workflows/ci.yml \
    .github/workflows/website.yml .github/workflows/publish.yml .github/workflows/release-rollout.yml 2>&1)"
  [ $? -eq 0 ] && ok "8. actionlint: workflow выпуска, кнопки, проверки, сайта и раскатки без замечаний" || bad "8. actionlint нашел проблемы" "$out"
else
  ok "8. Workflow выпуска: только по тегу, версия и текст из скриптов, долгий прогон коммита тега, повтор обновляет выпуск"
  skip "8а. actionlint не установлен — синтаксис workflow проверит GitHub"
fi

# ── 8б. Кнопка «Выпустить версию» ─────────────────────────────────────────────
# Отправки токеном GITHUB_TOKEN workflow не запускают (кроме workflow_dispatch): кнопка сама запускает проверку master и
# выпуск на теге, выпуск по кнопке сам запускает сайт, а workflow_run сайта такой выпуск пропускает — иначе сайт получил
# бы его дважды. Проверки и отправку делает тот же release.sh, а не копия в YAML.
BTN=".github/workflows/release-button.yml"
prob=()
if [ ! -f "$BTN" ]; then
  prob+=("нет $BTN")
else
  grep -qxF 'name: Выпустить версию' "$BTN" || prob+=("кнопка называется не «Выпустить версию»")
  grep -qE '^  workflow_dispatch:' "$BTN" || prob+=("кнопка запускается не через workflow_dispatch")
  grep -qE '^  (push|pull_request|schedule|workflow_run):' "$BTN" && prob+=("у кнопки лишний триггер — выпуск только по нажатию")
  grep -qE '^      version:' "$BTN" || prob+=("нет поля version")
  grep -A3 -E '^      dry_run:' "$BTN" | grep -qF 'type: boolean' || prob+=("нет флажка dry_run")
  grep -qF '[ "$GITHUB_REF" = refs/heads/master ]' "$BTN" || prob+=("нет отказа вне master")
  grep -qF 'fetch-depth: 0' "$BTN" || prob+=("checkout без всей истории и тегов")
  for line in 'git config user.name "Roman Burov"' 'git config user.email "romanburov89@gmail.com"' \
              'git config commit.gpgsign false' 'git config tag.gpgsign false'; do
    grep -qF "$line" "$BTN" || prob+=("нет «$line»")
  done
  grep -qE '^  contents: write' "$BTN" || prob+=("нет contents: write")
  grep -qE '^  actions: write' "$BTN" || prob+=("нет actions: write")
  grep -qE '^concurrency:' "$BTN" || prob+=("два выпуска могут идти сразу — нет concurrency")
  grep -qF 'scripts/release.sh "$VERSION"' "$BTN" || prob+=("выпуск не через scripts/release.sh")
  grep -qF 'flags=(--yes)' "$BTN" || prob+=("release.sh без --yes")
  grep -qE -- '--(skip-tests|allow-dirty)' "$BTN" && prob+=("кнопка обходит проверки release.sh")
  grep -qF 'gh workflow run ci.yml --repo "$GITHUB_REPOSITORY" --ref master' "$BTN" || prob+=("кнопка не запускает проверку master")
  grep -qF 'gh workflow run release.yml --repo "$GITHUB_REPOSITORY" --ref "$tag"' "$BTN" || prob+=("кнопка не запускает выпуск на теге")
  # Значения полей — только через env и в заголовках: ${{ inputs.* }} внутри скрипта — подстановка текста в shell.
  inj="$(grep -nE '\$\{\{[^}]*inputs\.' "$BTN" | grep -vE '^[0-9]+:[[:space:]]*(run-name|name|if|[A-Z_]+):')"
  [ -z "$inj" ] && grep -qF '${{ inputs.version }}' "$BTN" || prob+=("значение поля подставлено прямо в скрипт: $inj")
fi
grep -qE '^  workflow_dispatch:' "$WF" || prob+=("release.yml не запускается кнопкой (workflow_dispatch)")
grep -qF '[ "$GITHUB_REF_TYPE" = tag ]' "$WF" || prob+=("release.yml по кнопке не проверяет, что запущен на теге")
grep -qE '^concurrency:' "$WF" || prob+=("два запуска одного тега идут наперегонки — нет concurrency в release.yml")
grep -qF "if: github.event_name == 'workflow_dispatch'" "$WF" && grep -qF 'gh workflow run website.yml' "$WF" \
  || prob+=("выпуск по кнопке не запускает сайт")
grep -qF "github.event.workflow_run.event == 'push'" .github/workflows/website.yml \
  || prob+=("сайт берет workflow_run и от выпуска по кнопке — отправка задвоится")
grep -qE '^  workflow_dispatch:' .github/workflows/ci.yml || prob+=("ci.yml не запускается кнопкой")
grep -qF '"workflow_dispatch"' scripts/release-gate.py || prob+=("release-gate.py не принимает проверку, запущенную кнопкой")
if [ ${#prob[@]} -eq 0 ]; then
  ok "8б. Кнопка: только с master, release.sh --yes, автор — владелец, без подписи; запускает проверку master и выпуск тега, сайт — один раз"
else
  bad "8б. Кнопка «Выпустить версию»" "${prob[@]}"
fi

# ── 8в. Выпуск на сайт: порядок команд, только манифест ──────────────────────
# Программа берет обновления с сайта: манифест (update) уходит последним, после подписи (update-sig) и всех файлов ленты
# (import, asset), иначе сайт откажет или отдаст манифест без файлов. Шаги «Файлы выпуска и проверка сумм» и «Отправить
# на сайт» из website.yml идут здесь как есть, с поддельными gh (файлы выпуска — из папки стенда) и ssh (пишет команду и
# SHA-256 принятого файла; ssh-keygen — заглушка: ключ здесь не проверяется). Раскатка шлет только подпись и манифест;
# выпуск без подписи — без ленты; бета, порча, чужой манифест — без отправки. Нужен jq (он же в шаге).
SITE_WF=".github/workflows/website.yml"
SITE="$FIXTURE/site"
# step_script <имя шага>: текст run этого шага website.yml без отступа.
step_script() {
  awk -v name="      - name: $1" '
    $0 == name { inside = 1; next }
    inside && !body && /^        run: \|$/ { body = 1; next }
    body && NF && !/^          / { exit }
    body { sub(/^          /, ""); print }
  ' "$SITE_WF"
}
# make_release <папка> <версия>: выпуск, как его собирает release.yml — APK, update.json и подпись в SHA256SUMS.
make_release() {
  local d="$1" v="$2" assets='{}' pair f
  rm -rf "$d" && mkdir -p "$d" || return 1
  echo msi > "$d/PrivateGateway-$v-win-x64.msi"
  echo dmg > "$d/PrivateGateway-$v-osx-arm64.dmg"
  echo deb > "$d/private-gateway_${v}_amd64.deb"
  echo rpm > "$d/private-gateway-$v-1.x86_64.rpm"
  echo tar > "$d/PrivateGateway-$v-linux-x64.tar.gz"
  echo apk > "$d/PrivateGateway-$v-android.apk"
  (cd "$d" && sha256sum "PrivateGateway-$v-android.apk" > "PrivateGateway-$v-android.apk.sha256")
  for pair in "win-x64-msi:PrivateGateway-$v-win-x64.msi" "osx-arm64-dmg:PrivateGateway-$v-osx-arm64.dmg" \
              "linux-x64-deb:private-gateway_${v}_amd64.deb" "linux-x64-rpm:private-gateway-$v-1.x86_64.rpm" \
              "linux-x64-tar:PrivateGateway-$v-linux-x64.tar.gz" "android-apk:PrivateGateway-$v-android.apk"; do
    f="${pair#*:}"
    assets="$(jq --arg k "${pair%%:*}" --arg f "$f" --arg s "$(sha256sum "$d/$f" | cut -d' ' -f1)" \
      --argjson n "$(wc -c < "$d/$f")" '.[$k] = {file: $f, sha256: $s, size: $n}' <<< "$assets")" || return 1
  done
  jq -n --arg v "$v" --argjson a "$assets" '{version: $v, channel: "stable", rollout: 100,
    published: "2026-11-13T00:00:00Z", notes: "https://private-gateway.duckdns.org/downloads", highlights: ["Новое."],
    assets: $a}' > "$d/update.json" || return 1
  echo 'MEUCIQDsaXRlLXRlc3Q=' > "$d/update.json.sig"
  resum "$d"
}
# resum <папка>: SHA256SUMS заново — все файлы, кроме .apk.sha256 (как assemble).
resum() {
  local f names=()
  for f in "$1"/*; do
    case "${f##*/}" in SHA256SUMS | *.apk.sha256) ;; *) names+=("${f##*/}") ;; esac
  done
  (cd "$1" && sha256sum "${names[@]}" > SHA256SUMS)
}
# site_run <тег> [true]: два шага website.yml на выпуске из $SITE/rel; команды сайта — в $SITE/ssh.log.
site_run() {
  local work="$SITE/run"
  rm -rf "$work" && mkdir -p "$work/tmp" "$work/home" && : > "$work/output" && : > "$work/summary" \
    && : > "$SITE/ssh.log" && : > "$SITE/gh.log" || return 1
  (
    export PATH="$SITE/bin:$PATH" HOME="$work/home" RUNNER_TEMP="$work/tmp" GITHUB_OUTPUT="$work/output" \
      GITHUB_STEP_SUMMARY="$work/summary" GITHUB_REPOSITORY=rvburov/private-gateway-client GH_TOKEN=test \
      SITE_LOG="$SITE" FAKE_RELEASE="$SITE/rel" TAG="$1" MANIFEST_ONLY="${2:-}"
    bash -c "$files_step" > "$work/files.log" 2>&1 || exit 1
    ! grep -qx 'skip=true' "$work/output" || exit 0
    DIR="$(sed -n 's/^dir=//p' "$work/output")" WEBSITE_HOST=example.org WEBSITE_USER=releases \
      WEBSITE_SSH_KEY=site-test WEBSITE_KNOWN_HOSTS='example.org ssh-ed25519 AAAA' \
      bash -c "$send_step" > "$work/send.log" 2>&1
  )
}
# sent: команды сайта по порядку, без суммы; matched: у каждой команды сумма — SHA-256 переданного файла.
sent() { sed -E 's/ [0-9a-f]{64} \| [0-9a-f]{64}$//' "$SITE/ssh.log"; }
matched() { awk '{ if ($(NF - 2) != $NF) bad = 1 } END { exit bad }' "$SITE/ssh.log"; }
prob=()
ran=""
if ! command -v jq > /dev/null 2>&1; then
  skip "8в. Нет jq — шаги website.yml не прогнаны (их проверит запуск на GitHub)"
else
  ran=1
  mkdir -p "$SITE/bin"
  files_step="$(step_script 'Файлы выпуска и проверка сумм')"
  send_step="$(step_script 'Отправить на сайт')"
  [ -n "$files_step" ] && [ -n "$send_step" ] || prob+=("в $SITE_WF не найдены шаги «Файлы выпуска и проверка сумм» и «Отправить на сайт»")
  cat > "$SITE/bin/gh" <<'GH'
#!/bin/bash
# gh release download <тег> --repo … --dir <папка> --pattern <шаблон>…: файлы «выпуска» из $FAKE_RELEASE.
[ "$1 $2" = "release download" ] || exit 2
tag="$3"; shift 3; dir=""; patterns=()
while [ $# -gt 0 ]; do
  case "$1" in
    --dir) dir="$2"; shift 2 ;;
    --repo) shift 2 ;;
    --pattern) patterns+=("$2"); shift 2 ;;
    *) exit 2 ;;
  esac
done
echo "$tag ${patterns[*]}" >> "$SITE_LOG/gh.log"
n=0
for f in "$FAKE_RELEASE"/*; do
  for p in "${patterns[@]}"; do
    # shellcheck disable=SC2053 # шаблон, как у gh
    if [[ "${f##*/}" == $p ]]; then cp "$f" "$dir/" && n=$((n + 1)); break; fi
  done
done
[ "$n" -gt 0 ] || { echo "no assets match the file pattern" >&2; exit 1; }
GH
  cat > "$SITE/bin/ssh" <<'SSH'
#!/bin/bash
# ssh … <пользователь@сервер> <команда> < файл: команда сайта и SHA-256 того, что ушло в stdin.
printf '%s | %s\n' "${*: -1}" "$(sha256sum | cut -d' ' -f1)" >> "$SITE_LOG/ssh.log"
SSH
  cat > "$SITE/bin/ssh-keygen" <<'KEYGEN'
#!/bin/bash
# ssh-keygen -y -f <ключ> и -lf <открытый>: ключ стенда принимается без проверки.
case "$1" in
  -y) echo 'ssh-ed25519 AAAAsitetest site-test' ;;
  -lf) echo '256 SHA256:site-test site-test (ED25519)' ;;
  *) exit 2 ;;
esac
KEYGEN
  chmod +x "$SITE/bin/gh" "$SITE/bin/ssh" "$SITE/bin/ssh-keygen"
  rel="$SITE/rel"
  order='import windows 2.0.0 PrivateGateway-2.0.0-win-x64.msi
import macos 2.0.0 PrivateGateway-2.0.0-osx-arm64.dmg
import linux 2.0.0 private-gateway_2.0.0_amd64.deb
import android 2.0.0 PrivateGateway-2.0.0-android.apk
asset 2.0.0 private-gateway-2.0.0-1.x86_64.rpm
update-sig 2.0.0 update.json.sig
update 2.0.0 update.json'

  make_release "$rel" 2.0.0 || prob+=("стенд выпуска не собрался")
  if ! site_run v2.0.0; then
    prob+=("выпуск не ушел на сайт: $(cat "$SITE/run/files.log" "$SITE/run/send.log" 2> /dev/null | tail -n 3)")
  else
    [ "$(sent)" = "$order" ] || prob+=("порядок не «установщики → rpm → подпись → манифест»: $(sent | tr '\n' ';')")
    matched || prob+=("сумма в команде не совпала с переданным файлом")
  fi
  if ! site_run v2.0.0 true; then
    prob+=("только манифест не ушел: $(tail -n 3 "$SITE/run/files.log")")
  else
    [ "$(sent)" = "$(printf '%s\n' "$order" | tail -n 2)" ] || prob+=("только манифест: не подпись и манифест: $(sent | tr '\n' ';')")
    matched || prob+=("только манифест: сумма не та")
    grep -qE 'msi|apk' "$SITE/gh.log" && prob+=("только манифест качает установщики")
  fi

  rm "$rel/update.json.sig" && resum "$rel"
  site_run v2.0.0 && [ "$(sent)" = "$(printf '%s\n' "$order" | head -n 5)" ] && grep -q 'без подписи' "$SITE/run/summary" \
    || prob+=("выпуск без подписи: не установщики без ленты")
  site_run v2.0.0 true && [ ! -s "$SITE/ssh.log" ] && grep -qx 'skip=true' "$SITE/run/output" \
    || prob+=("выпуск без подписи, только манифест: не пропуск без отправки")

  make_release "$rel" 2.0.0 && rm "$rel/update.json.sig"
  ! site_run v2.0.0 true && [ ! -s "$SITE/ssh.log" ] || prob+=("подпись есть в SHA256SUMS, а файла нет — не остановка")
  make_release "$rel" 2.0.0 && echo порча >> "$rel/PrivateGateway-2.0.0-win-x64.msi"
  ! site_run v2.0.0 && [ ! -s "$SITE/ssh.log" ] || prob+=("порченый установщик не остановил отправку")
  make_release "$rel" 2.0.0 && jq '.version = "1.9.9"' "$rel/update.json" > "$SITE/m.json" && mv "$SITE/m.json" "$rel/update.json" \
    && resum "$rel"
  ! site_run v2.0.0 true && [ ! -s "$SITE/ssh.log" ] || prob+=("манифест другой версии не остановил отправку")
  make_release "$rel" 2.0.0 && jq --arg s "$(printf '0%.0s' {1..64})" '.assets["android-apk"].sha256 = $s' "$rel/update.json" \
    > "$SITE/m.json" && mv "$SITE/m.json" "$rel/update.json" && resum "$rel"
  ! site_run v2.0.0 true && [ ! -s "$SITE/ssh.log" ] || prob+=("файл ленты в манифесте не тот, что в SHA256SUMS, — не остановка")
  make_release "$rel" 2.0.0 && rm "$rel/PrivateGateway-2.0.0-osx-arm64.dmg"
  ! site_run v2.0.0 && [ ! -s "$SITE/ssh.log" ] || prob+=("файла ленты нет в выпуске — манифест ушел бы без него")
  make_release "$rel" 2.0.0-beta.1
  site_run v2.0.0-beta.1 && [ ! -s "$SITE/ssh.log" ] && grep -qx 'skip=true' "$SITE/run/output" \
    || prob+=("бета ушла на сайт")
fi
RO=".github/workflows/release-rollout.yml"
grep -qE '^  workflow_call:' "$SITE_WF" || prob+=("website.yml не вызывается из других workflow (workflow_call)")
grep -qF 'uses: ./.github/workflows/website.yml' "$RO" && grep -qE '^      manifest-only: true$' "$RO" \
  || prob+=("раскатка не отдает сайту новую долю (website.yml с manifest-only)")
[ "$(grep -n 'artifacts/builds/release-Android' "$WF" | head -n 1 | cut -d: -f1)" -lt \
  "$(grep -n 'release.cs assemble' "$WF" | head -n 1 | cut -d: -f1)" ] 2> /dev/null \
  || prob+=("APK не попадает к сборкам до release.cs assemble — в манифесте не будет android-apk")
grep -qE 'release\.cs assemble .* 100$' "$WF" || prob+=("выпуск выходит не на 100 % (решение владельца 2026-11-13)")
if [ ${#prob[@]} -eq 0 ] && [ -n "$ran" ]; then
  ok "8в. Сайт: установщики → rpm → подпись → манифест последним; раскатка — только подпись и манифест; без подписи — без ленты; бета, порча и чужой манифест не уходят; APK — в манифест, выпуск на 100 %"
elif [ ${#prob[@]} -eq 0 ]; then
  ok "8в. Сайт (по тексту): website.yml вызывается раскаткой с manifest-only, APK — в манифест, выпуск на 100 %"
else
  bad "8в. Выпуск на сайт" "${prob[@]}"
fi

# ── 9. Повтор выпуска и тег перед подписью ──────────────────────────────────
# Выпуск 0.1.5 остановился перед подписью: checkout кладет тег простой ссылкой, а release.sh ставит аннотированный, и
# «git fetch» без «+» отказался его заменить. Здесь тот же порядок на стенде; затем тег удаляют и выпуск повторяют.
prob=()
clone="$FIXTURE/clone"
git clone -q "$FIXTURE/origin.git" "$clone" 2>/dev/null || prob+=("клон стенда не создан")
git -C "$clone" tag -d v1.4.2 > /dev/null 2>&1 && git -C "$clone" tag v1.4.2 "$(git -C "$FIXTURE/origin.git" rev-parse 'v1.4.2^{commit}')"
git -C "$clone" fetch -q origin master "refs/tags/v1.4.2:refs/tags/v1.4.2" 2>/dev/null \
  && prob+=("без «+» простой тег заменился аннотированным — проверка ничего не доказывает")
git -C "$clone" fetch -q origin master "+refs/tags/v1.4.2:refs/tags/v1.4.2" 2>/dev/null \
  || prob+=("с «+» аннотированный тег не встал на место простого")
[ "$(git -C "$clone" cat-file -t v1.4.2 2>/dev/null)" = tag ] || prob+=("после fetch тег не аннотированный")

# Повтор: тег удален на «GitHub» и локально, тот же день — нового коммита нет, тег встает на текущий master.
fixture_git push -q origin :refs/tags/v1.4.2 2>/dev/null && fixture_git tag -d v1.4.2 > /dev/null
head_before="$(fixture_git rev-parse HEAD)"
out="$(echo y | bash "$SRC/scripts/release.sh" 1.4.2 --skip-tests 2>&1)" || prob+=("повтор выпуска упал: $out")
[ "$(fixture_git rev-parse HEAD)" = "$head_before" ] || prob+=("повтор выпуска сделал лишний коммит")
[ "$(fixture_git rev-parse 'v1.4.2^{commit}' 2>/dev/null)" = "$head_before" ] || prob+=("тег повтора не на текущем коммите")
git -C "$FIXTURE/origin.git" rev-parse -q --verify refs/tags/v1.4.2 > /dev/null || prob+=("тег повтора не отправлен")
[ "$(grep -c '^\[1\.4\.2\]:' "$SRC/CHANGELOG.md")" = 1 ] || prob+=("ссылка версии задвоилась")
if [ ${#prob[@]} -eq 0 ]; then
  ok "9. Тег перед подписью качается с «+»; повтор выпуска после удаления тега — без лишнего коммита и двойной ссылки"
else
  bad "9. Повтор выпуска и тег перед подписью" "${prob[@]}"
fi

# ── 9а. Обрыв при отправке тега ─────────────────────────────────────────────
# Выпуск 0.2.0: master ушел, а соединение для тега GitHub оборвал. Хук origin стенда отклоняет первую отправку тега —
# release.sh повторяет сам; если отказ не проходит, говорит, как отправить тег руками, и master при этом уже на месте.
prob=()
hook="$FIXTURE/origin.git/hooks/pre-receive"
cat > "$hook" <<HOOK
#!/bin/bash
while read -r _ new ref; do
  # Удаление тега (новый SHA из нулей) стенд пропускает: отказывает только отправке
  [ "\${new//0/}" = "" ] && continue
  case "\$ref" in refs/tags/*)
    [ -e "$FIXTURE/always-reject" ] && { echo "Connection closed (стенд)" >&2; exit 1; }
    [ -e "$FIXTURE/rejected-once" ] || { touch "$FIXTURE/rejected-once"; echo "Connection closed (стенд)" >&2; exit 1; } ;;
  esac
done
HOOK
chmod +x "$hook"
fixture_git push -q origin :refs/tags/v1.4.2 2>/dev/null && fixture_git tag -d v1.4.2 > /dev/null
out="$(echo y | PG_RELEASE_PUSH_DELAYS="0 0" bash "$SRC/scripts/release.sh" 1.4.2 --skip-tests 2>&1)" \
  || prob+=("выпуск с одним обрывом упал: $out")
[ -e "$FIXTURE/rejected-once" ] || prob+=("хук стенда не отклонил первую отправку — проверка ничего не доказывает")
printf '%s\n' "$out" | grep -qF 'повтор через 0 с' || prob+=("нет сообщения о повторе: $out")
git -C "$FIXTURE/origin.git" rev-parse -q --verify refs/tags/v1.4.2 > /dev/null || prob+=("после повтора тега нет на origin")
# Отказ на каждой попытке: понятная подсказка, master на origin, тег только локально.
fixture_git push -q origin :refs/tags/v1.4.2 2>/dev/null && fixture_git tag -d v1.4.2 > /dev/null
touch "$FIXTURE/always-reject"
out="$(echo y | PG_RELEASE_PUSH_DELAYS="0 0" bash "$SRC/scripts/release.sh" 1.4.2 --skip-tests 2>&1)" \
  && prob+=("выпуск без отправки тега завершился успехом")
printf '%s\n' "$out" | grep -qF 'git push origin v1.4.2' || prob+=("нет команды для ручной отправки: $out")
printf '%s\n' "$out" | grep -qF 'Если отправка тега оборвалась' || prob+=("нет ссылки на порядок в чеклисте: $out")
[ "$(printf '%s\n' "$out" | grep -c 'повтор через')" = 2 ] || prob+=("попыток не три (первая и два повтора): $out")
[ "$(git -C "$FIXTURE/origin.git" rev-parse master)" = "$(fixture_git rev-parse HEAD)" ] || prob+=("master не на origin")
fixture_git rev-parse -q --verify refs/tags/v1.4.2 > /dev/null || prob+=("тег не остался локально")
grep -qF 'Если отправка тега оборвалась' docs/operations/checklist.md || prob+=("в чеклисте нет раздела, на который ссылается подсказка")
rm -f "$hook" "$FIXTURE/always-reject"
if [ ${#prob[@]} -eq 0 ]; then
  ok "9а. Обрыв при отправке тега: release.sh повторяет сам; не прошло — подсказка с командой, master уже на месте"
else
  bad "9а. Обрыв при отправке тега" "${prob[@]}"
fi

# ── 9б. Без вопроса: --yes ────────────────────────────────────────────────────
# Кнопка на GitHub работает без терминала. Без --yes закрытый ввод — отказ с подсказкой, а не «Отменено» с кодом 0
# (зеленый запуск без выпуска); с --yes — тот же выпуск, что после «y», и ввод не читается: «n» на входе его не отменяет.
prob=()
# После 9а тег v1.4.2 остался только на стенде (его отправку отклоняли) — сначала он уходит на origin, как руками по
# подсказке.
fixture_git push -q origin v1.4.2 2>/dev/null || prob+=("тег v1.4.2 не отправлен на origin стенда")
awk '{ print } /^## \[Не выпущено\]/ { print ""; print "## [1.4.3]"; print ""; print "### Исправлено"; print ""; print "- Выпуск кнопкой." }' \
  "$SRC/CHANGELOG.md" > "$FIXTURE/history" && mv "$FIXTURE/history" "$SRC/CHANGELOG.md"
fixture_git add CHANGELOG.md && fixture_commit 'Описание 1.4.3' && fixture_git push -q origin master \
  || prob+=("не удалось подготовить 1.4.3 на стенде")
before="$(fixture_state)"
out="$(bash "$SRC/scripts/release.sh" 1.4.3 --skip-tests < /dev/null 2>&1)" && prob+=("закрытый ввод без --yes — успех: $out")
printf '%s\n' "$out" | grep -qF -- 'с ключом --yes' || prob+=("закрытый ввод без подсказки про --yes: $out")
out="$(echo n | bash "$SRC/scripts/release.sh" 1.4.3 --dry-run --yes --skip-tests 2>&1)" || prob+=("--dry-run --yes упал: $out")
[ "$(fixture_state)" = "$before" ] || prob+=("отказ или --dry-run --yes изменили стенд")
out="$(echo n | bash "$SRC/scripts/release.sh" 1.4.3 --yes --skip-tests 2>&1)" || prob+=("--yes упал: $out")
printf '%s\n' "$out" | grep -qF 'Выпустить v1.4.3? — да (--yes)' || prob+=("нет строки о согласии по --yes: $out")
printf '%s\n' "$out" | grep -qF 'Отменено' && prob+=("«n» на входе отменило выпуск с --yes")
[ "$(fixture_git log -1 --format=%s)" = "release: v1.4.3" ] || prob+=("последний коммит — не «release: v1.4.3»")
[ "$(fixture_git log -1 --format='%an <%ae> %cn <%ce>')" = 'Roman Burov <romanburov89@gmail.com> Roman Burov <romanburov89@gmail.com>' ] \
  || prob+=("автор или коммитер коммита выпуска — не владелец")
[ "$(fixture_git cat-file -t v1.4.3 2>/dev/null)" = tag ] || prob+=("тег v1.4.3 не аннотированный или его нет")
[ "$(fixture_git rev-parse 'v1.4.3^{commit}' 2>/dev/null)" = "$(fixture_git rev-parse HEAD)" ] || prob+=("тег не на коммите выпуска")
[ "$(git -C "$FIXTURE/origin.git" rev-parse master 2>/dev/null)" = "$(fixture_git rev-parse HEAD)" ] || prob+=("master не отправлен")
git -C "$FIXTURE/origin.git" rev-parse -q --verify refs/tags/v1.4.3 >/dev/null || prob+=("тег не отправлен")
grep -qF '<Version>1.4.3</Version>' "$SRC/Directory.Build.props" || prob+=("версия в Directory.Build.props не 1.4.3")
grep -qE "^## \[1\.4\.3\] — [0-9]{4}-[0-9]{2}-[0-9]{2}$" "$SRC/CHANGELOG.md" || prob+=("у секции 1.4.3 нет даты")
grep -qxF '[1.4.3]: https://github.com/rvburov/private-gateway-client/compare/v1.4.2...v1.4.3' "$SRC/CHANGELOG.md" \
  || prob+=("нет ссылки сравнения 1.4.2...1.4.3")
grep -qxF '[Не выпущено]: https://github.com/rvburov/private-gateway-client/compare/v1.4.3...HEAD' "$SRC/CHANGELOG.md" \
  || prob+=("ссылка «Не выпущено» не сдвинута")
[ -z "$(fixture_git status --porcelain)" ] || prob+=("после выпуска остались незакоммиченные файлы")
if [ ${#prob[@]} -eq 0 ]; then
  ok "9б. --yes: без вопроса тот же выпуск, что после «y»; без --yes закрытый ввод — отказ с подсказкой, стенд не тронут"
else
  bad "9б. Выпуск без вопроса (--yes)" "${prob[@]}"
fi

# ── 10. Репозиторий не тронут ──────────────────────────────────────────────────
[ "$(git status --porcelain)" = "$STATUS_BEFORE" ] && ok "10. Прогон тестов ничего не изменил в репозитории" \
  || bad "10. Прогон тестов изменил рабочее дерево"

echo
echo "Пройдено: $passed, пропущено: $skipped, провалено: $failed"
[ $failed -eq 0 ]
