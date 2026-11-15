#!/usr/bin/env python3
"""Жесткий срок, потомки с открытым stdout и безопасный выбор PID для стеков."""

import copy
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import time
import unittest


WATCHDOG = Path(__file__).parents[1] / "soak-watchdog.py"
SPEC = importlib.util.spec_from_file_location("soak_watchdog", WATCHDOG)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

# Форма настоящего --report-ctrf xUnit.net v3: поля окружения здесь не нужны.
PASSED_CTRF = {
    "reportFormat": "CTRF", "specVersion": "0.0.0",
    "results": {
        "summary": {"tests": 1, "passed": 1, "failed": 0, "pending": 0, "skipped": 0, "other": 0},
        "tests": [{"name": "Synthetic.Soak_proxy_mode_survives_failures_in_a_loop", "status": "passed", "duration": 1000}],
    },
}


class CtrfTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def write(self, report):
        (self.root / "one.ctrf").write_text(json.dumps(report))

    def test_one_real_format_pass_is_accepted(self):
        self.write(PASSED_CTRF)
        MODULE.passed_ctrf(self.root)

    def test_missing_malformed_unknown_and_multiple_reports_are_rejected(self):
        with self.assertRaises(ValueError):
            MODULE.passed_ctrf(self.root)
        path = self.root / "one.ctrf"
        for text in ("", "{", "[]", '{}', '{"reportFormat":"other"}'):
            with self.subTest(text=text):
                path.write_text(text)
                with self.assertRaises(ValueError):
                    MODULE.passed_ctrf(self.root)
        self.write(PASSED_CTRF)
        (self.root / "two.ctrf").write_text(json.dumps(PASSED_CTRF))
        with self.assertRaises(ValueError):
            MODULE.passed_ctrf(self.root)

    def test_skips_empty_failure_pending_unknown_and_noninteger_counts_are_rejected(self):
        for key, value in (("tests", 0), ("tests", True), ("passed", 0), ("failed", 1),
                           ("skipped", 1), ("pending", 1), ("other", 1), ("tests", "1")):
            with self.subTest(key=key, value=value):
                report = copy.deepcopy(PASSED_CTRF)
                report["results"]["summary"][key] = value
                self.write(report)
                with self.assertRaises(ValueError):
                    MODULE.passed_ctrf(self.root)
        report = copy.deepcopy(PASSED_CTRF)
        del report["results"]["summary"]["other"]
        self.write(report)
        with self.assertRaises(ValueError):
            MODULE.passed_ctrf(self.root)

    def test_test_records_must_confirm_the_summary(self):
        for tests in ([], [{"status": "skipped"}], [None], [{"status": "passed"}] * 2):
            with self.subTest(tests=tests):
                report = copy.deepcopy(PASSED_CTRF)
                report["results"]["tests"] = tests
                self.write(report)
                with self.assertRaises(ValueError):
                    MODULE.passed_ctrf(self.root)


def live(pid):
    try:
        # Убитый и еще не подобранный init процесс уже не выполняется.
        state = subprocess.check_output(["ps", "-o", "stat=", "-p", str(pid)], text=True).strip()
        return bool(state) and not state.startswith("Z")
    except subprocess.CalledProcessError:
        return False


@unittest.skipUnless(os.name == "posix", "watchdog использует собственную POSIX-группу")
class WatchdogTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.progress = self.root / "progress.txt"
        self.output = self.root / "out"
        self.collector = self.root / "collector"
        self.calls = self.root / "calls.txt"
        self.collector.write_text(
            f"#!{sys.executable}\n"
            "import pathlib, sys\n"
            f"pathlib.Path({str(self.calls)!r}).write_text(sys.argv[-1])\n"
            "print('Managed stack, test data only', flush=True)\n"
        )
        self.collector.chmod(0o755)

    def command(self, source, timeout=1.0, stack_after=0.3, idle=None, ctrf=None):
        script = self.root / "child.py"
        script.write_text(source)
        args = [sys.executable, str(WATCHDOG), "--timeout-seconds", str(timeout),
                "--stack-after-seconds", str(stack_after), "--terminate-grace-seconds", "0.15",
                "--stack-timeout-seconds", "0.15", "--progress", str(self.progress),
                "--output-dir", str(self.output), "--stack-tool", str(self.collector)]
        if idle is not None:
            args += ["--idle-seconds", str(idle)]
        if ctrf is not None:
            args += ["--require-passed-ctrf", str(ctrf)]
        args += ["--", sys.executable, str(script)]
        return args

    def test_exit_code_and_live_output_are_preserved(self):
        process = subprocess.Popen(self.command(
            "import time\nprint('first', flush=True)\ntime.sleep(0.4)\nraise SystemExit(7)\n",
            timeout=2, stack_after=1,
        ), stdout=subprocess.PIPE, text=True)
        started = time.monotonic()
        first = []
        while "first\n" not in first:
            first.append(process.stdout.readline())
            self.assertLess(time.monotonic() - started, 1.0, "вывод должен прийти до выхода ребенка")
        self.assertIsNone(process.poll(), "первый вывод не буферизуется до завершения")
        rest = process.communicate(timeout=4)[0]
        self.assertEqual(process.returncode, 7, rest)
        self.assertIn("first", (self.output / "output.log").read_text())

    def test_success_is_success(self):
        result = subprocess.run(self.command("print('done')\n"), capture_output=True, text=True, timeout=4)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("done", result.stdout)

    def test_ctrf_success_does_not_depend_on_ansi_summary(self):
        reports = self.root / "reports"
        source = (
            "import pathlib\n"
            f"folder = pathlib.Path({str(reports)!r})\nfolder.mkdir()\n"
            f"(folder/'one.ctrf').write_text({json.dumps(PASSED_CTRF)!r})\n"
            "print('\\x1b[32m  total: 1\\x1b[m', flush=True)\n"
        )
        result = subprocess.run(self.command(source, ctrf=reports), capture_output=True, text=True, timeout=4)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("CTRF подтвердил", result.stdout)

    def test_zero_exit_without_a_report_is_failure(self):
        result = subprocess.run(self.command("print('passed')\n", ctrf=self.root / "reports"),
                                capture_output=True, text=True, timeout=4)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)

    def test_old_report_is_rejected_before_the_command(self):
        (self.root / "old.ctrf").write_text(json.dumps(PASSED_CTRF))
        result = subprocess.run(self.command("print('must not run')\n", ctrf=self.root),
                                capture_output=True, text=True, timeout=4)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertNotIn("must not run", result.stdout)

    def test_timeout_kills_term_ignoring_parent_and_descendant(self):
        pid_file = self.root / "descendant.pid"
        source = (
            "import os, pathlib, signal, subprocess, sys, time\n"
            "signal.signal(signal.SIGTERM, signal.SIG_IGN)\n"
            "child = subprocess.Popen([sys.executable, '-c', "
            "'import signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); time.sleep(100)'])\n"
            f"pathlib.Path({str(pid_file)!r}).write_text(str(child.pid))\n"
            f"pathlib.Path({str(self.progress)!r}).write_text(f'pid={{os.getpid()}} stage=waiting\\n')\n"
            "print('started', flush=True)\ntime.sleep(100)\n"
        )
        started = time.monotonic()
        try:
            result = subprocess.run(self.command(source, timeout=0.6, stack_after=0.2),
                                    capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 124, result.stdout + result.stderr)
            self.assertLess(time.monotonic() - started, 3)
            self.assertIn("stage=waiting", result.stdout)
            self.assertIn("Принудительная", result.stdout)
            self.assertTrue(self.calls.exists(), result.stdout)
            self.assertEqual(len(list(self.output.glob("stacks-*.txt"))), 1)
            self.assertIn("Managed stack", next(self.output.glob("stacks-*.txt")).read_text())
            self.assertFalse(live(int(pid_file.read_text())), "потомок не должен пережить watchdog")
        finally:
            if pid_file.exists() and live(int(pid_file.read_text())):
                os.kill(int(pid_file.read_text()), signal.SIGKILL)

    def test_parent_exit_does_not_wait_forever_for_inherited_stdout(self):
        pid_file = self.root / "orphan.pid"
        source = (
            "import pathlib, subprocess, sys\n"
            "child = subprocess.Popen([sys.executable, '-c', "
            "'import signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); time.sleep(100)'])\n"
            f"pathlib.Path({str(pid_file)!r}).write_text(str(child.pid))\n"
            "print('parent done', flush=True)\n"
        )
        started = time.monotonic()
        try:
            result = subprocess.run(self.command(source, timeout=3, stack_after=2),
                                    capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertLess(time.monotonic() - started, 2, "EOF потомка не должен держать ожидание")
            self.assertFalse(live(int(pid_file.read_text())))
        finally:
            if pid_file.exists() and live(int(pid_file.read_text())):
                os.kill(int(pid_file.read_text()), signal.SIGKILL)

    def test_progress_pid_cannot_select_an_unrelated_process(self):
        self.progress.write_text(f"pid={os.getpid()} stage=synthetic\n")
        result = subprocess.run(self.command("import time\ntime.sleep(0.5)\n",
                                             timeout=1, stack_after=0.1),
                                capture_output=True, text=True, timeout=4)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(self.calls.exists(), "PID вне группы команды не передается сборщику")

    def test_stuck_collector_cannot_delay_the_hard_deadline(self):
        self.collector.write_text(f"#!{sys.executable}\nimport time\ntime.sleep(100)\n")
        source = (
            "import os, pathlib, time\n"
            f"pathlib.Path({str(self.progress)!r}).write_text(f'pid={{os.getpid()}} stage=wait\\n')\n"
            "time.sleep(100)\n"
        )
        started = time.monotonic()
        result = subprocess.run(self.command(source, timeout=0.5, stack_after=0.1),
                                capture_output=True, text=True, timeout=4)
        self.assertEqual(result.returncode, 124, result.stdout + result.stderr)
        self.assertLess(time.monotonic() - started, 2)
        self.assertIn("Сбор стеков превысил срок", result.stdout)

    def test_idle_progress_collects_stacks_before_the_regular_threshold(self):
        source = (
            "import os, pathlib, time\n"
            f"pathlib.Path({str(self.progress)!r}).write_text(f'pid={{os.getpid()}} stage=stuck\\n')\n"
            "time.sleep(100)\n"
        )
        started = time.monotonic()
        result = subprocess.run(self.command(source, timeout=3, stack_after=2, idle=0.2),
                                capture_output=True, text=True, timeout=4)
        self.assertEqual(result.returncode, 124, result.stdout + result.stderr)
        self.assertLess(time.monotonic() - started, 1.5)
        self.assertTrue(self.calls.exists(), "idle должен собрать стек до обычного порога")
        self.assertIn("Срок отсутствия прогресса истек", result.stdout)

    def test_ci_termination_also_cleans_the_child_group(self):
        process = subprocess.Popen(
            self.command("import time\ntime.sleep(100)\n", timeout=3, stack_after=2),
            stdout=subprocess.PIPE, text=True,
        )
        first = process.stdout.readline()
        child = int(re.search(r"pid=([0-9]+)", first).group(1))
        try:
            process.terminate()
            process.communicate(timeout=3)
            self.assertEqual(process.returncode, 128 + signal.SIGTERM)
            self.assertFalse(live(child))
        finally:
            if live(child):
                os.kill(child, signal.SIGKILL)


if __name__ == "__main__":
    unittest.main()
