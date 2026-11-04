#!/usr/bin/env python3
"""启动器退出合同；使用假游戏，不需要游戏程序集或 dotnet。"""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


LAUNCHER = Path(__file__).resolve().with_name("run-headless.sh")
FAKE_GAME = r'''#!/usr/bin/env python3
import json, os, sys, time
from pathlib import Path
root = Path(os.environ["STS2_SOLVER_TEST_DIR"])
case = json.loads(next((root / "requests").glob("*.json")).read_text())
log = Path(sys.argv[sys.argv.index("--log-file") + 1])
log.write_text("[Sts2Solver] INIT id=Sts2Solver\n"
               + ("GODOT_LOG_TAIL_MARKER\n" if case.get("log_marker") else ""))
print("FAKE_GAME_STDOUT", flush=True)
print("FAKE_GAME_STDERR", file=sys.stderr, flush=True)
if not case.get("missing_done"):
    (root / "results" / "done.json").write_text(json.dumps({
        "schemaVersion": 1, "ok": int(not case.get("failed")),
        "failed": int(bool(case.get("failed"))), "results": [], "totalMs": 1
    }))
time.sleep(case.get("delay", 1))
(root / "normal-exit.marker").write_text("finished cleanup")
sys.exit(case.get("exit_code", 0))
'''


class HeadlessExitTests(unittest.TestCase):
    def run_case(self, case):
        root = Path(tempfile.mkdtemp(prefix="headless-exit-"))
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        for name in ["game", "ritsu", "mod", "seed/SlayTheSpire2/default/1"]:
            (root / name).mkdir(parents=True)
        game = root / "game/SlayTheSpire2"
        game.write_text(FAKE_GAME)
        game.chmod(0o755)
        for name in ["ritsu/STS2-RitsuLib.dll", "ritsu/mod_manifest.json",
                     "mod/Sts2Solver.dll", "mod/Sts2Solver.json"]:
            (root / name).write_text("{}")
        (root / "seed/SlayTheSpire2/default/1/settings.save").write_text("{}")
        request = root / "case.json"
        request.write_text(json.dumps(case))
        results = root / "results"
        command = ["bash", str(LAUNCHER), "--instance", "test",
                   "--instance-root", str(root / "instances"),
                   "--failure-root", str(root / "failures"),
                   "--game-root", str(root / "game"),
                   "--ritsu-root", str(root / "ritsu"),
                   "--mod-dir", str(root / "mod"),
                   "--seed-data", str(root / "seed"),
                   "--request", str(request), "--results", str(results),
                   "--timeout-seconds", "3", "--shutdown-timeout-seconds", "2"]
        result = subprocess.run(command, capture_output=True, text=True, timeout=15)
        marker = (root / "instances/test/run/normal-exit.marker").exists()
        copied = (results / "done.json").exists()
        return (result.returncode, result.stdout + result.stderr, marker, copied,
                root / "failures")

    def test_waits_for_clean_exit(self):
        code, log, marker, copied, failures = self.run_case({})
        self.assertEqual(code, 0, log)
        self.assertTrue(marker, log)
        self.assertTrue(copied, log)
        self.assertIn("HEADLESS_EXIT code=0", log)
        self.assertFalse(failures.exists(), log)

    def test_already_exited_success_is_accepted(self):
        code, log, marker, copied, failures = self.run_case({"delay": 0})
        self.assertEqual(code, 0, log)
        self.assertTrue(marker, log)
        self.assertTrue(copied, log)
        self.assertIn("HEADLESS_EXIT code=0", log)

    def test_completed_results_do_not_hide_process_failure(self):
        code, log, marker, copied, failures = self.run_case({"exit_code": 139, "log_marker": True})
        self.assertNotEqual(code, 0, log)
        self.assertTrue(marker, log)
        self.assertTrue(copied, log)
        self.assertIn("HEADLESS_EXIT code=139 signal=SEGV", log)
        self.assertIn("HEADLESS_LAUNCHER_LOG", log)
        self.assertIn("FAKE_GAME_STDOUT", log)
        self.assertIn("HEADLESS_GODOT_LOG_TAIL", log)
        self.assertIn("GODOT_LOG_TAIL_MARKER", log)
        artifacts = [path for path in failures.iterdir() if path.is_dir()]
        self.assertEqual(len(artifacts), 1, log)
        artifact = artifacts[0]
        self.assertTrue((artifact / "launcher.log").is_file(), log)
        self.assertTrue((artifact / "godot-headless.log").is_file(), log)
        self.assertTrue((artifact / "run/game-exit.txt").is_file(), log)
        self.assertTrue((artifact / "run/results/done.json").is_file(), log)
        self.assertIn("game_exit=code=139 signal=SEGV", (artifact / "failure.txt").read_text())
        self.assertIn(f"HEADLESS_FAILURE_ARTIFACTS path={artifact}", log)

    def test_failed_tests_remain_failed(self):
        for exit_code in [0, 1]:
            with self.subTest(exit_code=exit_code):
                code, log, marker, copied, failures = self.run_case(
                    {"failed": True, "exit_code": exit_code})
                self.assertNotEqual(code, 0, log)
                self.assertTrue(marker, log)
                self.assertTrue(copied, log)

    def test_shutdown_timeout_is_failure(self):
        code, log, marker, copied, failures = self.run_case({"delay": 30})
        self.assertNotEqual(code, 0, log)
        self.assertFalse(marker, log)
        self.assertTrue(copied, log)
        self.assertIn("仍未退出", log)
        self.assertIn("HEADLESS_GAME_EXIT code=143 signal=TERM", log)
        self.assertIn("HEADLESS_EXIT code=143 signal=TERM", log)
        artifacts = [path for path in failures.iterdir() if path.is_dir()]
        self.assertEqual(len(artifacts), 1, log)
        self.assertIn("game_exit=code=143 signal=TERM",
                      (artifacts[0] / "failure.txt").read_text())

    def test_exit_without_results_is_failure(self):
        code, log, marker, copied, failures = self.run_case(
            {"missing_done": True, "delay": 0, "log_marker": True})
        self.assertNotEqual(code, 0, log)
        self.assertTrue(marker, log)
        self.assertFalse(copied, log)
        self.assertIn("HEADLESS_GAME_EXIT code=0", log)
        self.assertIn("HEADLESS_EXIT code=0", log)
        self.assertIn("HEADLESS_LAUNCHER_LOG", log)
        self.assertIn("HEADLESS_GODOT_LOG_TAIL", log)
        artifacts = [path for path in failures.iterdir() if path.is_dir()]
        self.assertEqual(len(artifacts), 1, log)


if __name__ == "__main__":
    unittest.main()
