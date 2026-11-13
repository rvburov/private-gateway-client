#!/bin/bash
#
# Выпуск Private Gateway на GitHub — локальная половина, по образцу SmartBase (Scripts/github-release/release.sh).
#
#   scripts/release.sh 0.1.5                 # выпустить версию 0.1.5
#   scripts/release.sh 0.1.5 --dry-run       # только проверки, ничего не менять
#   scripts/release.sh 0.1.5 --skip-tests    # без прогона desktop/tests/Unit
#   scripts/release.sh 0.1.5 --allow-dirty   # разрешить незакоммиченные правки
#
# Что делает: проверяет предусловия → ставит версию в Directory.Build.props (если она там другая) → проставляет дату в
# секции CHANGELOG.md и ссылки внизу → коммитит «release: v0.1.5» → ставит аннотированный тег v0.1.5 → отправляет master и
# тег. Собирает и публикует уже GitHub — .github/workflows/release.yml ловит тег: дожидается зеленой проверки этого
# коммита, гоняет долгий прогон, собирает три ОС и Android и выкладывает выпуск с текстом из scripts/release-notes.sh.
# Обычный git push выпуск не запускает.
#
# Сам скрипт ничего не собирает и не требует gh — только git (и dotnet для быстрых тестов).
# Порядок выпуска целиком — docs/architecture/release.md и docs/operations/checklist.md.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
PROPS="$ROOT/Directory.Build.props"
HISTORY="$ROOT/CHANGELOG.md"
REPO_URL="https://github.com/rvburov/private-gateway-client"

VERSION=""
DRY_RUN=0
SKIP_TESTS=0
ALLOW_DIRTY=0

for arg in "$@"; do
  case "$arg" in
    --dry-run)     DRY_RUN=1 ;;
    --skip-tests)  SKIP_TESTS=1 ;;
    --allow-dirty) ALLOW_DIRTY=1 ;;
    -h|--help)     sed -n '2,18p' "$0"; exit 0 ;;
    -*)            echo "Неизвестный ключ: $arg" >&2; exit 1 ;;
    *)             [ -z "$VERSION" ] && VERSION="$arg" || { echo "Лишний аргумент: $arg" >&2; exit 1; } ;;
  esac
done

die()  { echo "✗ $1" >&2; exit 1; }
info() { echo "  $1"; }
step() { echo "→ $1"; }

[ -n "$VERSION" ] || die "не указана версия. Пример: scripts/release.sh 0.1.5"

# ── 1. Формат версии ──────────────────────────────────────────────────────────
# Только X.Y.Z, как в SmartBase (решение пользователя 2026-11-10). Бету (1.2.3-beta.1) workflow выпуска по-прежнему
# понимает, но этим скриптом она не выпускается.
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] \
  || die "версия «${VERSION}» не в формате X.Y.Z (предвыпускные суффиксы не поддерживаются)"

cd "$ROOT" || die "не удалось перейти в $ROOT"

# ── 2. Ветка и чистота рабочего дерева ────────────────────────────────────────
BRANCH="$(git rev-parse --abbrev-ref HEAD)"
[ "$BRANCH" = "master" ] || die "текущая ветка «${BRANCH}», выпуски делаются из master"

if [ -n "$(git status --porcelain)" ]; then
  if [ $ALLOW_DIRTY -eq 1 ]; then
    echo "⚠ рабочее дерево не чистое, продолжаю из-за --allow-dirty"
  else
    git status --short >&2
    die "рабочее дерево не чистое — закоммитьте или уберите правки"
  fi
fi

# ── 3. Совпадение с GitHub ────────────────────────────────────────────────────
# Сеть может быть недоступна. В боевом запуске это отказ: без сверки с origin легко выпустить тег на устаревшем
# коммите. В --dry-run — предупреждение, чтобы прогонять проверки без сети.
NET_OK=1
git fetch --quiet origin master --tags 2>/dev/null || NET_OK=0

if [ $NET_OK -eq 1 ]; then
  LOCAL="$(git rev-parse HEAD)"
  REMOTE="$(git rev-parse origin/master)"
  if [ "$LOCAL" != "$REMOTE" ]; then
    git merge-base --is-ancestor "$REMOTE" "$LOCAL" \
      && die "есть неотправленные коммиты — сначала git push origin master" \
      || die "локальный master разошелся с origin/master — сначала git pull"
  fi
elif [ $DRY_RUN -eq 1 ]; then
  echo "⚠ origin недоступен: проверки против GitHub пропущены (--dry-run)"
else
  die "не удалось связаться с origin — проверьте сеть и доступ к $REPO_URL"
fi

# ── 4. Тег свободен ───────────────────────────────────────────────────────────
TAG="v$VERSION"
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null \
  && die "тег $TAG уже есть локально. Повторить сборку выпуска, не двигая тег: Actions → «Выпуск на GitHub» →
    запуск этого тега → «Re-run all jobs»"

if [ $NET_OK -eq 1 ] && git ls-remote --exit-code --tags origin "$TAG" >/dev/null 2>&1; then
  die "тег $TAG уже опубликован на GitHub — версия занята"
fi

# ── 5. Версия: не меньше отладочной и больше каждого выпуска ──────────────────
# В Directory.Build.props — версия отладочных сборок: после выпуска ее поднимают на следующую (чеклист выпуска), и
# сборки из исходников обновляют установленную службу. Поэтому версия выпуска может ей и равняться.
CURRENT="$(sed -nE 's#.*<Version>([^<]*)</Version>.*#\1#p' "$PROPS" | head -n1)"
[ -n "$CURRENT" ] || die "не удалось прочитать <Version> из Directory.Build.props"

ver_cmp() {   # печатает -1, 0 или 1: $1 меньше, равна или больше $2 (по X.Y.Z, суффикс беты отбрасывается)
  local IFS=.
  # shellcheck disable=SC2206
  local a=(${1%%-*}) b=(${2%%-*}) i
  for i in 0 1 2; do
    (( 10#${a[i]:-0} > 10#${b[i]:-0} )) && { echo 1; return; }
    (( 10#${a[i]:-0} < 10#${b[i]:-0} )) && { echo -1; return; }
  done
  echo 0
}
[ "$(ver_cmp "$VERSION" "$CURRENT")" -ge 0 ] || die "версия $VERSION меньше текущей $CURRENT в Directory.Build.props"

# Прошлый выпуск — по тегам на GitHub: локальный тег мог остаться от удаленного выпуска (git fetch --tags его не
# убирает), и тогда ссылка «сравнить» в истории вела бы на тег, которого на GitHub нет. Без сети (--dry-run) —
# по локальным.
remote_tags() {
  if [ $NET_OK -eq 1 ]; then
    git ls-remote --tags --refs origin 'v[0-9]*' | sed -E 's#.*refs/tags/##'
  else
    git tag -l 'v[0-9]*'
  fi
}
LAST_TAG=""
LAST=""
while read -r tag; do
  [ -n "$tag" ] || continue
  v="${tag#v}"
  if [ -z "$LAST" ] || [ "$(ver_cmp "$v" "$LAST")" -gt 0 ]; then LAST="$v"; LAST_TAG="$tag"; fi
done < <(remote_tags)
if [ -n "$LAST" ] && [ "$(ver_cmp "$VERSION" "$LAST")" -le 0 ]; then
  die "версия $VERSION не больше последнего выпуска $LAST_TAG"
fi

# ── 6. История выпусков готова ────────────────────────────────────────────────
"$HERE/check-version.sh" --changelog-only "$VERSION" || die "CHANGELOG.md не готов к выпуску.
    Перенесите пункты из «## [Не выпущено]» в новую секцию «## [$VERSION]» и опишите изменения для людей."

# ── 7. Быстрый гейт: модульные тесты ──────────────────────────────────────────
if [ $SKIP_TESTS -eq 0 ]; then
  step "dotnet test --project desktop/tests/Unit"
  dotnet test --project desktop/tests/Unit > /dev/null \
    || die "модульные тесты не прошли — выпуск отменен (подробно: dotnet test --project desktop/tests/Unit)"
else
  echo "⚠ модульные тесты пропущены (--skip-tests)"
fi

# ── 8. План и подтверждение ───────────────────────────────────────────────────
TODAY="$(date +%F)"
echo
echo "Выпуск Private Gateway $VERSION (в Directory.Build.props сейчас $CURRENT, прошлый выпуск — ${LAST_TAG:-нет})"
[ "$VERSION" = "$CURRENT" ] || info "Directory.Build.props  <Version> → $VERSION"
info "CHANGELOG.md          ## [$VERSION] → ## [$VERSION] — $TODAY, ссылки внизу"
info "коммит                release: $TAG"
info "тег                   $TAG (аннотированный)"
info "отправка              origin master, затем origin $TAG"
info "дальше                GitHub дождется проверки коммита, долгого прогона (около часа) и сборок трех ОС и Android"
echo

if [ $DRY_RUN -eq 1 ]; then
  echo "✓ проверки пройдены. Это --dry-run: ни один файл не изменен."
  exit 0
fi

read -r -p "Выпустить $TAG? [y/N] " answer
case "$answer" in
  [yYдД]) ;;
  *) echo "Отменено."; exit 0 ;;
esac

# ── 9. Версия и дата ──────────────────────────────────────────────────────────
# Через временный файл — одинаково с GNU и BSD sed (без sed -i).
step "версия и дата"
tmp="$(mktemp)"
sed -E "s#<Version>[^<]*</Version>#<Version>${VERSION}</Version>#" "$PROPS" > "$tmp" && mv "$tmp" "$PROPS"

# «## [1.2.3]» или «## [1.2.3] — старая дата» → «## [1.2.3] — сегодня». Разделитель @: в замене стоит «##», которое
# оборвало бы s#...#...#. Точки экранируются — иначе «1.2.3» совпало бы и с «1a2b3».
VERSION_RE="${VERSION//./\\.}"
tmp="$(mktemp)"
sed -E "s@^## \[${VERSION_RE}\].*\$@## [${VERSION}] — ${TODAY}@" "$HISTORY" > "$tmp" && mv "$tmp" "$HISTORY"

# Ссылки внизу истории: «Не выпущено» — сравнение с новой версией, у новой — сравнение с прошлым выпуском (первый
# выпуск — его страница).
if [ -n "$LAST_TAG" ]; then NEW_LINK="$REPO_URL/compare/$LAST_TAG...$TAG"; else NEW_LINK="$REPO_URL/releases/tag/$TAG"; fi
tmp="$(mktemp)"
awk -v new="$VERSION" -v tag="$TAG" -v url="$REPO_URL" -v link="$NEW_LINK" '
  # Ссылка этой версии уже есть (повтор выпуска после неудачи) — заменяется, а не задваивается.
  index($0, "[" new "]:") == 1 { next }
  /^\[Не выпущено\]:/ { print "[Не выпущено]: " url "/compare/" tag "...HEAD"; print "[" new "]: " link; done = 1; next }
  { print }
  END { if (!done) { print ""; print "[Не выпущено]: " url "/compare/" tag "...HEAD"; print "[" new "]: " link } }
' "$HISTORY" > "$tmp" && mv "$tmp" "$HISTORY"

"$HERE/check-version.sh" "$VERSION" || die "после правки версии остались расхождения"

# ── 10. Коммит, тег, отправка ─────────────────────────────────────────────────
step "коммит и тег"
git add "$PROPS" "$HISTORY"
# Повтор выпуска (тег удален после неудачи сборки) в тот же день: версия, дата и ссылки уже на месте — коммитить нечего,
# тег встает на текущий коммит master.
if git diff --cached --quiet; then
  info "версия, дата и ссылки уже на месте — тег на текущий коммит"
else
  git commit -q -m "release: $TAG" || die "коммит не удался"
fi
git tag -a "$TAG" -m "Private Gateway $VERSION"

# Порядок важен: если отправку master отклонили (кто-то успел раньше), тег на GitHub не уедет, и версию можно выпустить
# заново, ничего не разбирая на сервере.
step "push origin master"
git push --quiet origin master || die "отправку master отклонили. Тег $TAG создан локально; после
    git pull --rebase повторите: git push origin master && git push origin $TAG"

# Отправка тега — второе соединение подряд, и GitHub иногда обрывает его по пути («Connection closed by … port 22»,
# выпуск 0.2.0). master уже отправлен, тег стоит локально, повтор безопасен: тот же тег уже на месте — git ответит
# «up to date». Паузы 2, 4, 8, 16 с; PG_RELEASE_PUSH_DELAYS заменяет их (тест — без ожидания).
step "push origin $TAG"
pushed=0
for delay in ${PG_RELEASE_PUSH_DELAYS-2 4 8 16} done; do
  if git push --quiet origin "$TAG"; then pushed=1; break; fi
  [ "$delay" = done ] && break
  info "отправка тега не прошла — повтор через $delay с"
  sleep "$delay"
done
[ "$pushed" = 1 ] || die "тег $TAG не отправлен (master уже на GitHub, тег создан локально). Проверьте, что тег на коммите
    выпуска: git rev-parse --short \"$TAG^{}\" — и отправьте его: git push origin $TAG
    (порядок — docs/operations/checklist.md, «Если отправка тега оборвалась»)"

echo
echo "✓ $TAG отправлен. Выпуск собирает GitHub (около полутора-двух часов: проверка, долгий прогон, сборки):"
echo "  $REPO_URL/actions/workflows/release.yml"
echo "  $REPO_URL/releases/tag/$TAG"
