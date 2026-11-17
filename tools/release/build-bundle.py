#!/usr/bin/env python3
"""从两个固定 Git 提交构建单工坊条目安装包；游戏目录仅作为编译引用。"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
TARGETS = ("game-public", "game-public-beta")


def git(*args):
    return subprocess.check_output(["git", "-C", str(ROOT), *args], text=True).strip()


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def property_value(source, file, name):
    return ET.parse(source / file).findtext(f"./PropertyGroup/{name}")


def inspect_source(source, target, game):
    props = "src/Mod/GameTarget.props"
    actual = property_value(source, props, "SolverGameTarget")
    if actual != target:
        raise ValueError(f"{target} 来源的目标是 {actual}")
    game_version = property_value(source, props, "SolverGameVersion")
    native = read_json(game / "release_info.json")
    if native["version"].removeprefix("v") != game_version:
        raise ValueError(f"{target} 需要游戏 {game_version}，输入为 {native['version']}")
    manifest = read_json(source / "src/Mod/Sts2Solver.json")
    version = property_value(source, "Directory.Build.props", "SolverVersion")
    if manifest["version"] != version or manifest["min_game_version"] != game_version:
        raise ValueError(f"{target} 清单版本与构建配置不一致")
    return {"gameVersion": game_version, "version": version,
            "ritsuTarget": property_value(source, props, "RitsuLibReferenceTarget"),
            "gameCommit": native["commit"]}


def assemble(packages, output, provenance):
    public, beta = (packages[t] for t in TARGETS)
    manifests = [read_json(p / "Sts2Solver.json") for p in (public, beta)]
    if manifests[0]["version"] != manifests[1]["version"]:
        raise ValueError("两份 Runtime 的 Mod 版本不同")
    for name in ("id", "dependencies", "has_dll", "has_pck"):
        if manifests[0][name] != manifests[1][name]:
            raise ValueError(f"两份清单的 {name} 不同")
    for target, package in packages.items():
        runtimes = sorted(p.parent.name for p in package.glob("*/Sts2Solver.Runtime.dll"))
        if runtimes != [target]:
            raise ValueError(f"{target} 产物包含错误的 Runtime 目录: {runtimes}")
    bundle = output / "Sts2Solver"
    bundle.mkdir()
    # 加载器以较低版本游戏为引用构建；两边的加载器源码必须相同。
    shutil.copy2(public / "Sts2Solver.dll", bundle)
    for name in ("LICENSE", "README.md"):
        shutil.copy2(beta / name, bundle)
    manifest = manifests[1]
    manifest["min_game_version"] = manifests[0]["min_game_version"]
    (bundle / "Sts2Solver.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
    for target, package in packages.items():
        (bundle / target).mkdir()
        shutil.copy2(package / target / "Sts2Solver.Runtime.dll", bundle / target)
    provenance["files"] = {str(p.relative_to(bundle)): sha256(p)
                           for p in sorted(bundle.rglob("*")) if p.is_file()}
    (bundle / "build-info.json").write_text(json.dumps(provenance, ensure_ascii=False, indent=2) + "\n")
    archive = output / f"Sts2Solver-v{manifest['version']}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zipped:
        for path in sorted(bundle.rglob("*")):
            if path.is_file():
                zipped.write(path, path.relative_to(output))
    (output / "SHA256SUMS.txt").write_text(f"{sha256(archive)}  {archive.name}\n")
    return archive


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--public-ref", default="game-public")
    parser.add_argument("--beta-ref", default="main")
    parser.add_argument("--public-game", type=Path, required=True)
    parser.add_argument("--beta-game", type=Path, required=True)
    parser.add_argument("--ritsu-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="新建的产物与日志目录")
    args = parser.parse_args()
    refs = dict(zip(TARGETS, (args.public_ref, args.beta_ref)))
    commits = {target: git("rev-parse", "--verify", f"{ref}^{{commit}}") for target, ref in refs.items()}
    games = dict(zip(TARGETS, (args.public_game.resolve(), args.beta_game.resolve())))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    temp_root = ROOT / ".local"
    temp_root.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="bundle-build-", dir=temp_root) as temporary:
        workspace = Path(temporary)
        sources, metadata = {}, {}
        for target in TARGETS:
            source = workspace / target
            source.mkdir()
            snapshot = workspace / f"{target}.tar"
            subprocess.run(["git", "-C", str(ROOT), "archive", "--format=tar",
                            "--output", str(snapshot), commits[target]], check=True)
            with tarfile.open(snapshot) as archive:
                archive.extractall(source, filter="data")
            sources[target] = source
            metadata[target] = inspect_source(source, target, games[target])
            metadata[target]["sourceCommit"] = commits[target]
            print(f"{target}: {commits[target]} / game {metadata[target]['gameVersion']}", flush=True)
        if metadata[TARGETS[0]]["version"] != metadata[TARGETS[1]]["version"]:
            raise ValueError("两个分支的 Mod 版本必须一致")
        loader_sources = [{str(p.relative_to(source)): sha256(p)
                           for p in sorted((source / "src/Loader").rglob("*")) if p.is_file()}
                          for source in sources.values()]
        if not loader_sources[0] or loader_sources[0] != loader_sources[1]:
            raise ValueError("两个分支的共用加载器源码不同")
        packages = {}
        for target, source in sources.items():
            packages[target] = source / "artifacts/mod/Sts2Solver"
            env = dict(os.environ, TMPDIR=str(workspace))
            command = ["dotnet", "build", "src/Mod/Sts2Solver.csproj", "-c", "Release", "-v", "minimal", "--disable-build-servers",
                       f"-p:Sts2Dir={games[target]}", f"-p:RitsuWorkshopRoot={args.ritsu_root.resolve()}",
                       f"-p:RitsuLibReferenceTarget={metadata[target]['ritsuTarget']}",
                       "-p:EnableModTesting=false", "-p:ContinuousIntegrationBuild=true"]
            log_path = output / f"{target}-build.log"
            print(f"Building {target}; log: {log_path}", flush=True)
            with log_path.open("w") as log:
                subprocess.run(command, cwd=source, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
            print(f"{target}: build passed", flush=True)
        archive = assemble(packages, output, {"runtimes": metadata})
        print(f"Packaged: {archive}")


if __name__ == "__main__":
    main()
