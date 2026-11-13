#!/bin/bash
#
# Тесты выпуска по образцу SmartBase (Scripts/github-release/release-test.sh): scripts/release.sh, check-version.sh,
# release-notes.sh и триггеры .github/workflows/release.yml. Чистый bash и git: .NET, сеть и gh не нужны. Запускается в
# CI (задание «Стиль и формат») и вручную:
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
  out="$(actionlint "$WF" .github/workflows/soak.yml 2>&1)"
  [ $? -eq 0 ] && ok "8. actionlint: workflow выпуска без замечаний" || bad "8. actionlint нашел проблемы" "$out"
else
  ok "8. Workflow выпуска: только по тегу, версия и текст из скриптов, долгий прогон коммита тега, повтор обновляет выпуск"
  skip "8а. actionlint не установлен — синтаксис workflow проверит GitHub"
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

# ── 10. Репозиторий не тронут ──────────────────────────────────────────────────
[ "$(git status --porcelain)" = "$STATUS_BEFORE" ] && ok "10. Прогон тестов ничего не изменил в репозитории" \
  || bad "10. Прогон тестов изменил рабочее дерево"

echo
echo "Пройдено: $passed, пропущено: $skipped, провалено: $failed"
[ $failed -eq 0 ]
