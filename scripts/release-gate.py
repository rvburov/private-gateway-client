#!/usr/bin/env python3
"""Условия выпуска: точный коммит тега в master, зеленые CI и долгий прогон этого коммита."""

import argparse
import json
import re
import subprocess


def command(*args):
    return subprocess.check_output(args, text=True).strip()


def successful_run(runs, sha, workflow):
    # Берется последний запуск: старый зеленый не скрывает новое падение или незаконченный повтор.
    eligible = [run for run in runs if run["head_sha"] == sha and run["head_branch"] == "master"
                and run["event"] in ("push", "schedule", "workflow_dispatch")
                and run["path"] == ".github/workflows/" + workflow]
    if not eligible:
        raise ValueError(f"{workflow}: нет запуска на master для {sha}")
    run = max(eligible, key=lambda item: (item["run_number"], item.get("run_attempt", 1)))
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
    args = parser.parse_args()
    if not re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?", args.tag):
        raise ValueError("Тег — v<SemVer>")
    if not re.fullmatch(r"[0-9a-f]{40}", args.sha):
        raise ValueError("Коммит — полный SHA")

    tagged = command("git", "rev-parse", "refs/tags/" + args.tag + "^{commit}")
    if tagged != args.sha:
        raise ValueError("Тег уже указывает на другой коммит: выпуск остановлен")
    subprocess.run(["git", "merge-base", "--is-ancestor", tagged, "origin/master"], check=True)

    for workflow in ("ci.yml", "soak.yml"):
        endpoint = (f"repos/{args.repository}/actions/workflows/{workflow}/runs"
                    f"?head_sha={tagged}&branch=master&per_page=100")
        pages = json.loads(command("gh", "api", "--paginate", "--slurp", endpoint))
        run = successful_run([run for page in pages for run in page["workflow_runs"]], tagged, workflow)
        pages = json.loads(command("gh", "api", "--paginate", "--slurp",
                                  f'repos/{args.repository}/actions/runs/{run["id"]}/jobs?per_page=100'))
        check_jobs([job for page in pages for job in page["jobs"]], workflow)
        print(f'{workflow}: {run["html_url"]} — успешно на {tagged}')


if __name__ == "__main__":
    try:
        main()
    except (ValueError, subprocess.CalledProcessError) as error:
        raise SystemExit(f"Выпуск остановлен: {error}") from error
