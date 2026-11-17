"""安装包契约：目录、版本、来源、校验值及压缩包必须一致。"""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("build_bundle", Path(__file__).with_name("build-bundle.py"))
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)


class BundleTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.output = self.root / "output"
        self.output.mkdir()
        self.packages = {}
        for target, game in zip(bundle.TARGETS, ("0.111.1", "0.113.1")):
            package = self.root / target
            (package / target).mkdir(parents=True)
            (package / target / "Sts2Solver.Runtime.dll").write_bytes(target.encode())
            for name in ("Sts2Solver.dll", "LICENSE", "README.md"):
                (package / name).write_text(f"{target}/{name}")
            manifest = dict(id="Sts2Solver", version="0.2.10", min_game_version=game,
                            dependencies=[dict(id="STS2-RitsuLib", min_version="0.6.11")],
                            has_dll=True, has_pck=False)
            (package / "Sts2Solver.json").write_text(json.dumps(manifest))
            self.packages[target] = package

    def test_zip_is_installable_and_matches_provenance(self):
        archive = bundle.assemble(self.packages, self.output, {"runtimes": {"commit": "fixed"}})
        with zipfile.ZipFile(archive) as zipped:
            names = set(zipped.namelist())
            self.assertEqual(names, {"Sts2Solver/" + name for name in (
                "Sts2Solver.dll", "Sts2Solver.json", "LICENSE", "README.md", "build-info.json",
                "game-public/Sts2Solver.Runtime.dll", "game-public-beta/Sts2Solver.Runtime.dll")})
            manifest = json.loads(zipped.read("Sts2Solver/Sts2Solver.json"))
            self.assertEqual(manifest["min_game_version"], "0.111.1")
            self.assertEqual(zipped.read("Sts2Solver/Sts2Solver.dll"), b"game-public/Sts2Solver.dll")
            info = json.loads(zipped.read("Sts2Solver/build-info.json"))
            for name, expected in info["files"].items():
                self.assertEqual(bundle.hashlib.sha256(zipped.read("Sts2Solver/" + name)).hexdigest(), expected)
        self.assertEqual((self.output / "SHA256SUMS.txt").read_text().split()[0], bundle.sha256(archive))

    def test_mixed_mod_versions_are_rejected_before_packaging(self):
        path = self.packages["game-public"] / "Sts2Solver.json"
        manifest = bundle.read_json(path)
        manifest["version"] = "0.2.9"
        path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, "版本不同"):
            bundle.assemble(self.packages, self.output, {})
        self.assertFalse((self.output / "Sts2Solver").exists())

    def test_wrong_runtime_is_rejected(self):
        package = self.packages["game-public"]
        (package / "game-public").rename(package / "game-public-beta")
        with self.assertRaisesRegex(ValueError, "错误的 Runtime"):
            bundle.assemble(self.packages, self.output, {})

    def test_mixed_dependencies_are_rejected(self):
        path = self.packages["game-public"] / "Sts2Solver.json"
        manifest = bundle.read_json(path)
        manifest["dependencies"] = []
        path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, "dependencies"):
            bundle.assemble(self.packages, self.output, {})

    def test_wrong_game_is_rejected_before_build(self):
        source, game = self.root / "source", self.root / "game"
        (source / "src/Mod").mkdir(parents=True)
        game.mkdir()
        (source / "src/Mod/GameTarget.props").write_text(
            "<Project><PropertyGroup><SolverGameTarget>game-public</SolverGameTarget>"
            "<SolverGameVersion>0.111.1</SolverGameVersion></PropertyGroup></Project>")
        (game / "release_info.json").write_text(json.dumps({"version": "v0.113.1"}))
        with self.assertRaisesRegex(ValueError, "需要游戏 0.111.1"):
            bundle.inspect_source(source, "game-public", game)


if __name__ == "__main__":
    unittest.main()
