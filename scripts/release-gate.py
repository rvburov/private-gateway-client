#!/usr/bin/env python3
"""Условия выпуска: точный коммит тега в master и зеленая проверка CI этого коммита.

Тег ставит scripts/release.sh сразу после коммита «release: vX.Y.Z», поэтому проверка этого коммита в момент тега еще
идет: с --wait условие ждет ее конца (не дольше заданных минут), упавшая проверка останавливает выпуск сразу. Долгий
прогон на коммите тега выпуск гоняет сам (задание «Долгий прогон» в release.yml), здесь его не ищут.
"""

import argparse
import json
import re
import subprocess
import time


def command(*args):
    return subprocess.check_output(args, text=True).strip()


def latest_run(runs, sha, workflow):
    # Берется последний запуск: старый зеленый не скрывает новое падение или незаконченный повтор.
    eligible = [run for run in runs if run["head_sha"] == sha and run["head_branch"] == "master"
                and run["event"] in ("push", "schedule", "workflow_dispatch")
                and run["path"] == ".github/workflows/" + workflow]
    return max(eligible, key=lambda item: (item["run_number"], item.get("run_attempt", 1))) if eligible else None


def successful_run(runs, sha, workflow):
    run = latest_run(runs, sha, workflow)
    if run is None:
        raise ValueError(f"{workflow}: нет запуска на master для {sha}")
    if run["status"] != "completed" or run["conclusion"] != "success":
        raise ValueError(f'{workflow}: последний запуск {run["html_url"]} — '
                         f'{run["status"]}/{run["conclusion"]}; дождитесь успеха и повторите выпуск')
    return run


def check_jobs(jobs, workflow):
    if not jobs or any(job["status"] != "completed" or job["conclusion"] != "success" for job in jobs):
        raise ValueError(f"{workflow}: есть незавершенные, упавшие или пропущенные задания")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("repository")
    parser.add_argument("tag")
    parser.add_argument("sha")
    parser.add_argument("--wait", type=int, default=0, metavar="МИНУТ",
                        help="ждать конца проверки этого коммита, не дольше стольких минут")
    args = parser.parse_args()
    if not re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?", args.tag):
        raise ValueError("Тег — v<SemVer>")
    if not re.fullmatch(r"[0-9a-f]{40}", args.sha):
        raise ValueError("Коммит — полный SHA")

    tagged = command("git", "rev-parse", "refs/tags/" + args.tag + "^{commit}")
    if tagged != args.sha:
        raise ValueError("Тег уже указывает на другой коммит: выпуск остановлен")
    subprocess.run(["git", "merge-base", "--is-ancestor", tagged, "origin/master"], check=True)

    for workflow in ("ci.yml",):
        endpoint = (f"repos/{args.repository}/actions/workflows/{workflow}/runs"
                    f"?head_sha={tagged}&branch=master&per_page=100")
        deadline = time.monotonic() + args.wait * 60
        while True:
            pages = json.loads(command("gh", "api", "--paginate", "--slurp", endpoint))
            runs = [run for page in pages for run in page["workflow_runs"]]
            pending = latest_run(runs, tagged, workflow)
            if (pending is not None and pending["status"] == "completed") or time.monotonic() >= deadline:
                break
            print(f"{workflow}: проверка {tagged[:12]} еще идет — жду", flush=True)
            time.sleep(30)
        run = successful_run(runs, tagged, workflow)
        pages = json.loads(command("gh", "api", "--paginate", "--slurp",
                                  f'repos/{args.repository}/actions/runs/{run["id"]}/jobs?per_page=100'))
        check_jobs([job for page in pages for job in page["jobs"]], workflow)
        print(f'{workflow}: {run["html_url"]} — успешно на {tagged}')


if __name__ == "__main__":
    try:
        main()
    except (ValueError, subprocess.CalledProcessError) as error:
        raise SystemExit(f"Выпуск остановлен: {error}") from error
