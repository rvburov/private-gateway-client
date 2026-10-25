#!/bin/bash
#
# Проверка scripts/worktree.sh на временном репозитории — то, что ROADMAP (этап 0) требует от
# рабочих папок: две папки параллельно, коммиты, sync, отказ merge без sync, merge, отказ remove с
# незакоммиченным, remove. Запускается в CI на Ubuntu, macOS и Windows (Git Bash) и вручную:
#
#   scripts/tests/worktree-smoke.sh
#
# Настоящий репозиторий не трогает: все происходит в mktemp -d, папка удаляется в конце.
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
export CLAUDE_CODE_REMOTE=          # проверяется режим компьютера, а не облачной копии
unset PG_BASE_BRANCH

git init -q --bare "$TMP/origin.git"
git init -q -b master "$TMP/main"
cd "$TMP/main"
git config user.name 'Smoke Test'
git config user.email 'smoke@example.com'
git config commit.gpgsign false
mkdir -p scripts
cp "$ROOT/scripts/worktree.sh" scripts/
printf 'a\n' > a.txt
printf 'b\n' > b.txt
printf '/.agents/\n' > .gitignore
git add -A
git commit -qm 'база'
git remote add origin ../origin.git
git push -q origin master 2> /dev/null || echo "  (push во временный origin не прошел — проверки с origin пропускаются)"

W=scripts/worktree.sh
fails=0

# expect <ожидаемая строка> <команда…>: команда может завершиться ошибкой — важен ее вывод.
expect() {
  local want="$1" out
  shift
  out=$("$@" 2>&1) || true
  if grep -qF -- "$want" <<< "$out"; then
    echo "ok   $*"
  else
    echo "FAIL $* — нет «${want}». Вывод:"
    sed 's/^/     /' <<< "$out"
    fails=$((fails + 1))
  fi
}

in_dir() { (cd "$1" && shift && "$@"); }

expect "Создана рабочая папка" $W new task-a
expect "Создана рабочая папка" $W new task-b
expect "имя — латиница" $W new Плохое_Имя

in_dir .agents/worktrees/task-a bash -c "echo a2 >> a.txt && git commit -qam 'a: правка'"
in_dir .agents/worktrees/task-b bash -c "echo b2 >> b.txt && git commit -qam 'b: правка'"

expect "НЕ В master коммитов: 1" $W check
expect "требует внимания мест: 2" $W check

expect "стоит на свежем master" in_dir .agents/worktrees/task-a $W sync
expect "В master влито коммитов: 1" in_dir .agents/worktrees/task-a $W merge
expect "master ушел вперед" in_dir .agents/worktrees/task-b $W merge
expect "стоит на свежем master" in_dir .agents/worktrees/task-b $W sync
expect "В master влито коммитов: 1" in_dir .agents/worktrees/task-b $W merge

if [[ "$(git log --format=%s master | head -2 | tr '\n' '|')" == "b: правка|a: правка|" ]]; then
  echo "ok   master линейный: обе правки по порядку"
else
  echo "FAIL master: $(git log --oneline master | head -3 | tr '\n' ' ')"
  fails=$((fails + 1))
fi

expect "Создана рабочая папка" $W new task-c
echo c >> .agents/worktrees/task-c/a.txt
expect "есть незакоммиченное" $W remove task-c
git -C .agents/worktrees/task-c checkout -- a.txt

expect "Убраны папка" $W remove task-a
expect "Убраны папка" $W remove task-b
expect "Убраны папка" $W remove task-c
expect "ИТОГ: все в master" $W check

echo
if (( fails == 0 )); then
  echo "worktree.sh: все проверки прошли"
else
  echo "worktree.sh: не прошло проверок: $fails"
  exit 1
fi
