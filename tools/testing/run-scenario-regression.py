#!/usr/bin/env python3
"""Generate and run the long-lived scenario request corpus.

The manifest in tests/corpora/scenarios/manifest.json is the single place that defines a
scenario: its fixture root, controller scenario name, mode/threads/language and any life or
enemy-HP adjustment needed to reach the target turn. Requests are generated into .local so the
repository does not keep dozens of near-identical JSON copies.

Each headless instance receives at most six requests and always uses
--cleanup-instance-on-exit. Instances run concurrently while keeping the total declared search
thread count at or below eight. The final summary lists each group's pass count and the first
failed assertion for every failing scenario.
"""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any


REPO = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO / "tests/corpora/scenarios/manifest.json"
GENERATED_ROOT = REPO / ".local/scenario-regression"
LOG_ROOT = GENERATED_ROOT / "logs"
DIFFERENTIAL = REPO / "artifacts/bin/Differential/release/Differential.dll"
MOD_DIR = REPO / "artifacts/mod-testing/Sts2Solver"
RUN_HEADLESS = REPO / "tools/testing/run-headless.sh"
MAX_REQUESTS_PER_INSTANCE = 6
MAX_SEARCH_THREADS = 8
ALL_GROUPS = ("controller", "turn-setup", "full-combat", "panel-stress")


@dataclass
class Entry:
    id: str
    scenario: str | None
    group: str
    fixture: str
    threads: int
    english: bool
    timeout_seconds: int
    separate: bool
    request: dict[str, Any]
    scenario_id: str | None


@dataclass
class Chunk:
    group: str
    index: int
    entries: list[Entry]

    @property
    def name(self) -> str:
        return f"{self.group}-{self.index:02d}"

    @property
    def max_threads(self) -> int:
        return max(entry.threads for entry in self.entries)


def fail(message: str) -> None:
    print(f"run-scenario-regression: {message}", file=sys.stderr)
    raise SystemExit(2)


def default_threads() -> int:
    try:
        logical = len(os.sched_getaffinity(0))
    except AttributeError:
        logical = os.cpu_count() or 1
    return max(1, min(8, logical - 2))


def load_entries(groups: list[str]) -> list[Entry]:
    manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    defaults = manifest.get("defaults", {})
    default_request = dict(defaults.get("request", {}))
    entries: list[Entry] = []
    for raw in manifest.get("scenarios", []):
        group = raw.get("group", defaults.get("group", "controller"))
        if group not in groups:
            continue
        threads_value = raw.get("threads", defaults.get("threads", 1))
        threads = default_threads() if threads_value == "auto" else int(threads_value)
        request = dict(default_request)
        request.update(raw.get("request", {}))
        entries.append(Entry(
            id=raw["id"],
            scenario=raw.get("scenario"),
            group=group,
            fixture=raw.get("fixture", defaults.get("fixture", "k3-basic-potions")),
            threads=threads,
            english=bool(raw.get("english", False)),
            timeout_seconds=int(raw.get("timeoutSeconds", defaults.get("timeoutSeconds", 900))),
            separate=bool(raw.get("separate", False)),
            request=request,
            scenario_id=raw.get("scenarioId"),
        ))
    unknown = {entry.group for entry in entries} - set(ALL_GROUPS)
    if unknown:
        fail(f"清单含未知分组：{sorted(unknown)}")
    if not entries:
        fail(f"没有可运行场景：groups={groups}")
    seen: set[str] = set()
    for entry in entries:
        if entry.id in seen:
            fail(f"场景 id 重复：{entry.id}")
        seen.add(entry.id)
    return entries


def chunks_for(entries: list[Entry]) -> list[Chunk]:
    chunks: list[Chunk] = []
    for group in ALL_GROUPS:
        members = [entry for entry in entries if entry.group == group]
        current: list[Entry] = []
        index = 1
        def flush() -> None:
            nonlocal current, index
            if not current:
                return
            chunks.append(Chunk(group, index, current))
            index += 1
            current = []
        for entry in members:
            if entry.separate:
                flush()
                chunks.append(Chunk(group, index, [entry]))
                index += 1
                continue
            current.append(entry)
            if len(current) == MAX_REQUESTS_PER_INSTANCE:
                flush()
        flush()
    return chunks


def load_base(fixture: str, cache: dict[str, dict[str, Any]]) -> dict[str, Any]:
    if fixture in cache:
        return json.loads(json.dumps(cache[fixture]))
    if not DIFFERENTIAL.is_file():
        fail(f"找不到 Differential：{DIFFERENTIAL}（先 dotnet build tests/Differential -c Release）")
    with tempfile.TemporaryDirectory(prefix="scenario-base-") as temporary:
        temporary_path = Path(temporary)
        result = subprocess.run(
            ["dotnet", str(DIFFERENTIAL), "--emit-live-requests", str(temporary_path),
             "--root", fixture],
            cwd=REPO, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            check=False)
        path = temporary_path / f"{fixture}.json"
        if result.returncode != 0 or not path.is_file():
            fail(f"生成 fixture 请求失败 root={fixture}:\n{result.stdout}")
        data = json.loads(path.read_text(encoding="utf-8"))
    cache[fixture] = data
    return json.loads(json.dumps(data))


def write_requests(chunks: list[Chunk], output_root: Path) -> None:
    if output_root.exists():
        shutil.rmtree(output_root)
    output_root.mkdir(parents=True)
    cache: dict[str, dict[str, Any]] = {}
    for chunk in chunks:
        directory = output_root / chunk.name
        directory.mkdir(parents=True)
        for entry in chunk.entries:
            request = load_base(entry.fixture, cache)
            request.update(entry.request)
            request["settingsThreadsForTest"] = entry.threads
            request["englishForTest"] = entry.english
            request["timeoutSeconds"] = entry.timeout_seconds
            if entry.scenario is not None:
                request["controllerScenarioForTest"] = entry.scenario
            if entry.scenario_id is not None:
                request["scenarioId"] = entry.scenario_id
            path = directory / f"{entry.id}.json"
            path.write_text(json.dumps(request, ensure_ascii=False, indent=2) + "\n",
                            encoding="utf-8")


def first_assertion(result_directory: Path, entry_id: str, fallback: str | None) -> str:
    controller = result_directory / entry_id / "controller.json"
    if controller.is_file():
        payload = json.loads(controller.read_text(encoding="utf-8"))
        for check in payload.get("checks", []):
            if check.get("status") == "failed":
                detail = check.get("detail")
                return f"{check.get('name')}: {detail}" if detail else str(check.get("name"))
        if payload.get("error"):
            return str(payload["error"])
    panel = result_directory / entry_id / "ui-panel.json"
    if panel.is_file():
        payload = json.loads(panel.read_text(encoding="utf-8"))
        failures = payload.get("failures") or []
        if failures:
            return str(failures[0])
        mismatches = payload.get("mismatches") or []
        if mismatches:
            return str(mismatches[0])
        if payload.get("error"):
            return str(payload["error"])
    return fallback or "request failed"


def read_ui_checks(result_directory: Path) -> str | None:
    path = result_directory / "ui-checks.json"
    if not path.is_file():
        return "ui-checks.json missing"
    payload = json.loads(path.read_text(encoding="utf-8"))
    if payload.get("status") == "ok" and int(payload.get("failed", 0)) == 0:
        return None
    for check in payload.get("checks", []):
        if check.get("status") == "failed":
            detail = check.get("error")
            return f"ui-checks {check.get('name')}: {detail}" if detail else f"ui-checks {check.get('name')}"
    return f"ui-checks status={payload.get('status')} failed={payload.get('failed')}"


@dataclass
class ChunkResult:
    chunk: Chunk
    ok: bool
    failures: list[tuple[str, str]]
    output: str
    failure_artifacts: list[str]
    invalid_task_ids: int
    build_invalid_task_ids: int


def run_chunk(chunk: Chunk, requests_root: Path, results_root: Path,
              timeout_seconds: int) -> ChunkResult:
    request_directory = requests_root / chunk.name
    result_directory = results_root / chunk.name
    log_directory = LOG_ROOT / chunk.name
    if result_directory.exists():
        shutil.rmtree(result_directory)
    if log_directory.exists():
        shutil.rmtree(log_directory)
    result_directory.parent.mkdir(parents=True, exist_ok=True)
    command = [
        str(RUN_HEADLESS),
        "--instance", f"scenario-{chunk.name}",
        "--requests", str(request_directory),
        "--results", str(result_directory),
        "--log-dir", str(log_directory),
        "--timeout-seconds", str(timeout_seconds),
        "--shutdown-timeout-seconds", "120",
        "--cleanup-instance-on-exit",
    ]
    started = time.monotonic()
    completed = subprocess.run(command, cwd=REPO, text=True, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, check=False)
    elapsed = time.monotonic() - started
    done_path = result_directory / "done.json"
    failures: list[tuple[str, str]] = []
    done_entries: dict[str, dict[str, Any]] = {}
    if done_path.is_file():
        done = json.loads(done_path.read_text(encoding="utf-8"))
        for item in done.get("results", []):
            done_entries[str(item.get("root"))] = item
    for entry in chunk.entries:
        item = done_entries.get(entry.id)
        if item is None:
            failures.append((entry.id, "没有结果（实例未完成或请求缺失）"))
            continue
        if item.get("status") != "ok":
            failures.append((entry.id, first_assertion(result_directory, entry.id,
                                                       item.get("error"))))
    if not done_path.is_file():
        failures.append((chunk.name, f"没有 done.json；run-headless exit={completed.returncode}"))
    elif completed.returncode != 0:
        failures.append((chunk.name, f"run-headless exit={completed.returncode}"))
    ui_failure = read_ui_checks(result_directory) if result_directory.exists() else None
    if ui_failure is not None:
        failures.append((chunk.name, ui_failure))
    failure_artifacts = []
    invalid_task_ids = 0
    build_invalid_task_ids = 0
    for line in completed.stdout.splitlines():
        marker = "HEADLESS_FAILURE_ARTIFACTS path="
        if line.startswith(marker):
            path = line[len(marker):].strip()
            if path and path not in failure_artifacts:
                failure_artifacts.append(path)
        invalid_marker = "HEADLESS_GODOT_ERROR "
        if line.startswith(invalid_marker):
            invalid = re.search(r"\binvalid_task_id=(\d+)", line)
            if invalid is not None:
                invalid_task_ids += int(invalid.group(1))
            build_invalid = re.search(r"\bbuild_invalid_task_id=(\d+)", line)
            if build_invalid is not None:
                build_invalid_task_ids += int(build_invalid.group(1))
    summary = (f"{chunk.name} requests={len(chunk.entries)} exit={completed.returncode} "
               f"elapsed={elapsed:.1f}s")
    if failures:
        summary += " failures=" + ",".join(entry_id for entry_id, _ in failures)
    if failure_artifacts:
        summary += " failureArtifacts=" + ",".join(failure_artifacts)
    summary += (f" invalidTaskIds={invalid_task_ids}"
                f" buildInvalidTaskIds={build_invalid_task_ids}")
    return ChunkResult(chunk, not failures, failures,
                       completed.stdout + "\nSCENARIO_CHUNK " + summary,
                       failure_artifacts, invalid_task_ids, build_invalid_task_ids)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--group", action="append", choices=(*ALL_GROUPS, "all"),
                        help="只跑一个分组，可重复；all 或省略为全部")
    parser.add_argument("--jobs", type=int, default=4,
                        help="最多同时运行的实例数（默认 4）")
    parser.add_argument("--results", type=Path,
                        default=GENERATED_ROOT / "results",
                        help="结果目录（默认 .local/scenario-regression/results）")
    parser.add_argument("--timeout-seconds", type=int, default=2400,
                        help="每个实例的超时秒数（默认 2400）")
    parser.add_argument("--scenario", action="append",
                        help="只跑指定场景 id，可重复（调试用）")
    parser.add_argument("--generate-only", action="store_true",
                        help="只生成请求，不启动无头实例")
    parser.add_argument("--list-groups", action="store_true", help="列出分组后退出")
    args = parser.parse_args()
    groups = ([] if not args.group or "all" in args.group else args.group) or list(ALL_GROUPS)
    if args.list_groups:
        for group in ALL_GROUPS:
            print(group)
        return 0
    if args.jobs < 1:
        fail("--jobs 必须至少为 1")
    if not MOD_DIR.is_dir():
        fail(f"找不到测试版 Mod：{MOD_DIR}（先 dotnet build src/Mod -c Release -p:EnableModTesting=true）")

    entries = load_entries(groups)
    if args.scenario:
        wanted = set(args.scenario)
        missing = wanted - {entry.id for entry in entries}
        if missing:
            fail(f"清单里没有场景：{sorted(missing)}")
        entries = [entry for entry in entries if entry.id in wanted]
    chunks = chunks_for(entries)
    requests_root = GENERATED_ROOT / "requests"
    write_requests(chunks, requests_root)
    if args.generate_only:
        print(f"SCENARIO_GENERATED chunks={len(chunks)} requests={len(entries)} "
              f"out={requests_root}")
        return 0
    args.results.mkdir(parents=True, exist_ok=True)
    # Remove this run's old chunk directories (a previous manifest may have had more chunks),
    # but leave other groups' results alone so --group runs do not disturb them.
    for group in groups:
        for stale in args.results.glob(f"{group}-*"):
            if stale.is_dir():
                shutil.rmtree(stale)

    entry_counts = {group: sum(1 for entry in entries if entry.group == group)
                    for group in groups}
    print("SCENARIO_PLAN " + " ".join(
        f"{group}={entry_counts[group]}" for group in groups if entry_counts[group] > 0)
        + f" chunks={len(chunks)} jobs={args.jobs}")
    for chunk in chunks:
        print(f"SCENARIO_CHUNK_PLAN {chunk.name} requests={len(chunk.entries)} "
              f"threads={chunk.max_threads} entries=" +
              ",".join(entry.id for entry in chunk.entries))

    pending = list(chunks)
    running: dict[concurrent.futures.Future[ChunkResult], Chunk] = {}
    completed_results: list[ChunkResult] = []
    active_threads = 0
    lock = threading.Lock()
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as executor:
        while pending or running:
            for chunk in list(pending):
                with lock:
                    can_start = (len(running) < args.jobs
                                 and active_threads + chunk.max_threads <= MAX_SEARCH_THREADS)
                if not can_start:
                    continue
                pending.remove(chunk)
                active_threads += chunk.max_threads
                future = executor.submit(run_chunk, chunk, requests_root, args.results,
                                         args.timeout_seconds)
                running[future] = chunk
            if not running:
                if pending:
                    fail("调度器无法启动任何实例（检查线程预算）")
                break
            done, _ = concurrent.futures.wait(
                running, timeout=1.0, return_when=concurrent.futures.FIRST_COMPLETED)
            for future in done:
                chunk = running.pop(future)
                active_threads -= chunk.max_threads
                result = future.result()
                completed_results.append(result)
                for line in result.output.splitlines():
                    if line.startswith(("HEADLESS_", "SCENARIO_CHUNK ", "run-headless:",
                                        "INSTANCE_REMOVED")):
                        print(line)
                for path in result.failure_artifacts:
                    print(f"SCENARIO_FAILURE_ARTIFACTS chunk={chunk.name} path={path}")
                if not result.ok:
                    print(f"SCENARIO_CHUNK_FAILED {chunk.name}")

    by_group = {group: [result for result in completed_results
                       if result.chunk.group == group] for group in groups}
    total_entries = sum(entry_counts.values())
    total_failures = 0
    for group in groups:
        expected = entry_counts[group]
        results = by_group[group]
        failures = [failure for result in results for failure in result.failures]
        total_failures += len(failures)
        # A failed chunk with ui-checks also counts as a failure, but scenarios may already be
        # counted; report both the scenario total and the chunk-level failure list.
        failed_scenarios = {entry_id for entry_id, _ in failures
                            if entry_id in {entry.id for entry in entries if entry.group == group}}
        print(f"GROUP {group} passed={expected - len(failed_scenarios)}/{expected} "
              f"failed={len(failed_scenarios)}")
        for entry_id, detail in failures:
            if entry_id in failed_scenarios:
                scenario = next(entry.scenario or entry.id for entry in entries
                                if entry.id == entry_id)
                print(f"FAIL group={group} scenario={scenario} id={entry_id} "
                      f"assertion={detail}")
            else:
                print(f"FAIL group={group} instance={entry_id} assertion={detail}")
    all_artifacts = [path for result in completed_results
                     for path in result.failure_artifacts]
    total_invalid_task_ids = sum(result.invalid_task_ids for result in completed_results)
    total_build_invalid_task_ids = sum(result.build_invalid_task_ids
                                       for result in completed_results)
    summary = (f"SCENARIO_SUMMARY total={total_entries} failed={total_failures} "
               f"groups={len(groups)} invalidTaskIds={total_invalid_task_ids} "
               f"buildInvalidTaskIds={total_build_invalid_task_ids} "
               f"results={args.results}")
    if all_artifacts:
        summary += " failureArtifacts=" + ",".join(all_artifacts)
    print(summary)
    return 1 if total_failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
