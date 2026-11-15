#!/usr/bin/env python3
"""Диагностический прогон: живой вывод, стеки без дампа памяти и жесткий срок процесса.

Выпускные проверки не меняет. Команда и ее потомки запускаются в собственной группе;
таймаут всегда означает ошибку, даже если приложение затем завершилось штатно.
"""

import argparse
import math
import os
from pathlib import Path
import re
import selectors
import signal
import subprocess
import sys
import time


def seconds(value):
    parsed = float(value)
    if not math.isfinite(parsed) or parsed <= 0:
        raise argparse.ArgumentTypeError("срок должен быть положительным конечным числом")
    return parsed


def group_alive(group):
    try:
        os.killpg(group, 0)
        return True
    except ProcessLookupError:
        return False


def signal_group(group, value):
    try:
        os.killpg(group, value)
    except ProcessLookupError:
        pass


def process_group(group):
    """Только PID, группа и имя: окружение и аргументы процессов не читаются."""
    listing = subprocess.check_output(
        ["ps", "-axo", "pid=,ppid=,pgid=,comm="], text=True, timeout=5
    )
    found = {}
    for line in listing.splitlines():
        parts = line.split(maxsplit=3)
        if len(parts) == 4 and parts[0].isdigit() and parts[2] == str(group):
            found[int(parts[0])] = Path(parts[3]).name
    return found


def stack_targets(group, progress):
    members = process_group(group)
    # PID отметки принадлежит самому тестовому приложению, а не родителю dotnet test.
    preferred = []
    try:
        with progress.open("rb") as source:
            length = source.seek(0, os.SEEK_END)
            source.seek(max(0, length - 128 * 1024))
            content = source.read(128 * 1024).decode("utf-8", errors="replace")
        preferred = [int(value) for value in re.findall(r"\bpid=([0-9]+)\b", content)]
    except OSError:
        pass
    candidates = [pid for pid in reversed(preferred) if pid in members]
    candidates.extend(
        pid for pid, name in members.items()
        if name.startswith(("dotnet", "PrivateGateway", "testhost"))
    )
    return list(dict.fromkeys(candidates))[:8]


def collect_stacks(group, progress, tool, destination, timeout, deadline, log):
    if not tool:
        log("Инструмент стеков не настроен")
        return
    try:
        targets = stack_targets(group, progress)
    except (OSError, subprocess.SubprocessError):
        log("Не удалось получить список PID для стеков")
        return
    if not targets:
        log("Управляемые процессы группы для стеков не найдены")
    for pid in targets:
        budget = min(timeout, deadline - time.monotonic())
        if budget <= 0:
            return
        log(f"Сбор управляемых стеков: pid={pid}")
        path = destination / f"stacks-{pid}.txt"
        with path.open("wb") as output:
            try:
                # Никаких дампов памяти: report пишет только стеки управляемых потоков.
                collector = subprocess.Popen(
                    [tool, "report", "--process-id", str(pid)],
                    stdout=output, stderr=subprocess.STDOUT, start_new_session=True,
                )
            except OSError:
                log(f"Не удалось запустить сбор стеков: pid={pid}")
                continue
            try:
                code = collector.wait(timeout=budget)
                log(f"Сбор стеков завершен: pid={pid}, код={code}")
            except subprocess.TimeoutExpired:
                log(f"Сбор стеков превысил срок: pid={pid}")
                signal_group(collector.pid, signal.SIGKILL)
                try:
                    collector.wait(timeout=1)
                except subprocess.TimeoutExpired:
                    pass
            finally:
                # Сборщик также не должен оставлять собственные дочерние процессы.
                signal_group(collector.pid, signal.SIGKILL)


def run(args):
    args.output_dir.mkdir(parents=True, exist_ok=True)
    with (args.output_dir / "output.log").open("wb") as output:
        def emit(data):
            output.write(data)
            output.flush()
            os.write(sys.stdout.fileno(), data)

        def log(message):
            emit((f"[soak-watchdog] {message}\n").encode("utf-8"))

        started = time.monotonic()
        deadline = started + args.timeout_seconds
        process = subprocess.Popen(
            args.command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            start_new_session=True, bufsize=0,
        )
        log(f"Начат диагностический процесс: pid={process.pid}")
        selector = selectors.DefaultSelector()
        os.set_blocking(process.stdout.fileno(), False)
        selector.register(process.stdout, selectors.EVENT_READ)
        offset = 0
        last_progress = started
        stacks_taken = False
        timed_out = False
        stopping_at = None
        killed = False
        exited_at = None
        try:
            while True:
                now = time.monotonic()
                for key, _ in selector.select(timeout=0.05):
                    try:
                        chunk = os.read(key.fd, 64 * 1024)
                    except BlockingIOError:
                        continue
                    if chunk:
                        emit(chunk)
                    else:
                        selector.unregister(key.fileobj)

                try:
                    with args.progress.open("rb") as progress:
                        if progress.seek(0, os.SEEK_END) < offset:
                            offset = 0
                        progress.seek(offset)
                        chunk = progress.read(64 * 1024)
                        offset += len(chunk)
                        if chunk:
                            emit(chunk)
                            last_progress = now
                except FileNotFoundError:
                    pass

                code = process.poll()
                if code is not None and exited_at is None:
                    exited_at = now
                if not timed_out and code is None and now >= deadline:
                    timed_out = True
                    stopping_at = now
                    log("Жесткий срок истек; остановка всей группы процессов")
                    signal_group(process.pid, signal.SIGTERM)
                elif (not timed_out and code is None and args.idle_seconds is not None
                      and now - last_progress >= args.idle_seconds):
                    log("Отметки этапов не меняются; сбор стеков перед остановкой")
                    collect_stacks(
                        process.pid, args.progress, args.stack_tool, args.output_dir,
                        args.stack_timeout_seconds, deadline, log,
                    )
                    timed_out = True
                    stopping_at = time.monotonic()
                    log("Срок отсутствия прогресса истек; остановка всей группы процессов")
                    signal_group(process.pid, signal.SIGTERM)
                elif (not stacks_taken and code is None and not timed_out
                      and now - started >= args.stack_after_seconds):
                    stacks_taken = True
                    collect_stacks(
                        process.pid, args.progress, args.stack_tool, args.output_dir,
                        args.stack_timeout_seconds, deadline, log,
                    )

                # Родитель мог уйти, оставив потомка с открытым stdout. Не ждем его EOF бессрочно.
                if code is not None and stopping_at is None and now - exited_at >= 0.2:
                    if group_alive(process.pid):
                        log("Родитель завершился; остановка оставшихся потомков")
                        stopping_at = now
                        signal_group(process.pid, signal.SIGTERM)
                    else:
                        break
                if stopping_at is not None:
                    if not group_alive(process.pid):
                        break
                    if not killed and now - stopping_at >= args.terminate_grace_seconds:
                        log("Принудительная остановка всей группы процессов")
                        signal_group(process.pid, signal.SIGKILL)
                        killed = True
                    if killed and now - stopping_at >= args.terminate_grace_seconds + 1:
                        break
        finally:
            signal_group(process.pid, signal.SIGKILL)
            try:
                process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                pass
            selector.close()
            process.stdout.close()
        if timed_out:
            return 124
        code = process.returncode
        return code if code is not None and code >= 0 else 128 + abs(code or signal.SIGKILL)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout-seconds", type=seconds, default=2400)
    parser.add_argument("--stack-after-seconds", type=seconds, default=2100)
    parser.add_argument("--terminate-grace-seconds", type=seconds, default=5)
    parser.add_argument("--stack-timeout-seconds", type=seconds, default=30)
    parser.add_argument("--idle-seconds", type=seconds)
    parser.add_argument("--progress", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--stack-tool")
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.command[:1] == ["--"]:
        args.command = args.command[1:]
    if not args.command:
        parser.error("после -- нужна команда")
    if args.stack_after_seconds >= args.timeout_seconds:
        parser.error("сбор стеков должен начинаться до жесткого срока")
    if os.name != "posix":
        parser.error("диагностический watchdog предназначен для POSIX")

    def terminate(signum, _frame):
        # Отмена задания CI тоже проходит через finally с уборкой отдельной группы.
        raise SystemExit(128 + signum)

    signal.signal(signal.SIGTERM, terminate)
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
