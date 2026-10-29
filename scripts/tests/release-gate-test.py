#!/usr/bin/env python3
"""Проверка выбора доказательств выпуска: чужой SHA, ветка, упавший повтор и пропуск не подходят."""

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("release_gate", Path(__file__).parents[1] / "release-gate.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class ReleaseGateTest(unittest.TestCase):
    def run_data(self, **changes):
        return dict(head_sha="a" * 40, head_branch="master", event="push", path=".github/workflows/ci.yml",
                    run_number=1, run_attempt=1, status="completed", conclusion="success", html_url="https://example.com/run", **changes)

    def test_exact_sha_master_and_success_are_required(self):
        good = self.run_data()
        self.assertEqual(gate.successful_run([good], "a" * 40, "ci.yml"), good)
        for field, value in (("head_sha", "b" * 40), ("head_branch", "feature"), ("conclusion", "failure"),
                             ("status", "in_progress"), ("path", ".github/workflows/publish.yml")):
            with self.subTest(field=field), self.assertRaises(ValueError):
                gate.successful_run([good | {field: value}], "a" * 40, "ci.yml")

    def test_old_success_does_not_hide_failed_or_pending_rerun(self):
        good = self.run_data()
        for latest in (good | {"run_number": 2, "conclusion": "failure"},
                       good | {"run_attempt": 2, "status": "in_progress", "conclusion": None}):
            with self.subTest(latest=latest), self.assertRaises(ValueError):
                gate.successful_run([latest, good], "a" * 40, "ci.yml")

    def test_skipped_or_missing_jobs_are_rejected(self):
        gate.check_jobs([{"status": "completed", "conclusion": "success"}], "ci.yml")
        for jobs in ([], [{"status": "completed", "conclusion": "skipped"}],
                     [{"status": "in_progress", "conclusion": None}]):
            with self.subTest(jobs=jobs), self.assertRaises(ValueError):
                gate.check_jobs(jobs, "ci.yml")

    def test_tag_must_match_and_be_ancestor_of_master_before_api_is_called(self):
        with tempfile.TemporaryDirectory() as temp:
            def git(*args):
                return subprocess.check_output(["git", "-C", temp, *args], text=True, stderr=subprocess.DEVNULL).strip()

            git("init", "-b", "master")
            git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-m", "base")
            base = git("rev-parse", "HEAD")
            git("update-ref", "refs/remotes/origin/master", base)
            git("tag", "v0.1.0")
            git("checkout", "-b", "unmerged")
            git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-m", "unmerged")
            other = git("rev-parse", "HEAD")
            git("tag", "v0.2.0")
            original = os.getcwd()
            try:
                os.chdir(temp)
                with patch("sys.argv", ["release-gate.py", "owner/repo", "v0.1.0", other]), self.assertRaises(ValueError):
                    gate.main()

                def local_only(*args):
                    self.assertEqual(args[0], "git", "API не вызывается до проверки принадлежности master")
                    return subprocess.check_output(args, text=True).strip()
                with patch("sys.argv", ["release-gate.py", "owner/repo", "v0.2.0", other]), \
                        patch.object(gate, "command", side_effect=local_only), self.assertRaises(subprocess.CalledProcessError):
                    gate.main()

                # После проверок git оба workflow и все их задания должны подходить именно этому SHA.
                run = self.run_data() | {"head_sha": base, "id": 7}
                def api(*args):
                    if args[0] == "git":
                        return subprocess.check_output(args, text=True).strip()
                    self.assertEqual(args[:4], ("gh", "api", "--paginate", "--slurp"))
                    if "/jobs?" in args[-1]:
                        return json.dumps([{"jobs": [{"status": "completed", "conclusion": "success"}]}])
                    workflow = "soak.yml" if "/soak.yml/" in args[-1] else "ci.yml"
                    return json.dumps([{"workflow_runs": [run | {"path": ".github/workflows/" + workflow}]}])
                with patch("sys.argv", ["release-gate.py", "owner/repo", "v0.1.0", base]), patch.object(gate, "command", side_effect=api):
                    gate.main()
            finally:
                os.chdir(original)


if __name__ == "__main__":
    unittest.main()
