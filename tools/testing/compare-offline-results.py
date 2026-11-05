#!/usr/bin/env python3
"""Compare two Differential --offline-combat result directories.

For every root present in either directory the script reports:

* win/loss: only `outcome == "victory"` counts as a win;
* HP loss: `playerMaxHp - playerHp` for a win, and the full `playerMaxHp` for a
  loss or a missing/failed result (the task's "输的局按全失" rule).

It summarizes by character and, when request metadata is available, by deck
card. Deck metadata comes from `--requests-file` (the list produced by
`tools/testing/sample-sts2-sim.py`) or from `tests/corpora/quality/*/spec.json`
for quality roots (character only; the exact generated deck is not in the spec).

Example:

    python3 tools/testing/compare-offline-results.py \
      .local/pw1/eval-quality-after .local/pw1/eval-quality-before \
      --corpora tests/corpora --out .local/pw1/quality-compare.csv

    python3 tools/testing/compare-offline-results.py \
      .local/pw1/eval-sim-after .local/pw1/eval-sim-before \
      --requests-file .local/pw1/sample.txt --out .local/pw1/sim-compare.csv
"""

import argparse
import csv
import json
import sys
from collections import defaultdict
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("new_dir", help="offline-combat result directory (after)")
    parser.add_argument("old_dir", help="offline-combat result directory (before)")
    parser.add_argument("--requests-file", help="request list used for the run (deck metadata)")
    parser.add_argument("--corpora", default="tests/corpora",
                        help="quality corpora root for character metadata (default: tests/corpora)")
    parser.add_argument("--out", help="write a per-game CSV")
    return parser.parse_args()


def read_request_metadata(path: Path) -> dict:
    request = json.loads(path.read_text())
    cards = []
    for entry in request.get("runCards") or []:
        card_id = entry.get("cardId")
        if isinstance(card_id, str):
            cards.append(card_id)
    for entry in request.get("cards") or []:
        if entry.get("treatAsDeckCard"):
            card_id = entry.get("cardId")
            if isinstance(card_id, str):
                cards.append(card_id)
    return {
        "character": str(request.get("characterId", "")).upper(),
        "cards": sorted(set(cards)),
        "maxHp": request.get("initialPlayerMaxHp"),
    }


def read_request_list(path: Path) -> dict[str, dict]:
    metadata: dict[str, dict] = {}
    base = path.resolve().parent
    for raw in path.read_text().splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if "=" in line:
            name, request_path = line.split("=", 1)
            name = name.strip()
            request_path = request_path.strip()
        else:
            name = Path(line).stem
            request_path = line
        full = Path(request_path)
        if not full.is_absolute():
            full = base / full
        metadata[name] = read_request_metadata(full.resolve())
    return metadata


def read_quality_metadata(corpora: Path, root: str) -> dict | None:
    if not root.startswith("quality-"):
        return None
    name = root[len("quality-"):]
    spec_path = corpora / "quality" / name / "spec.json"
    if not spec_path.is_file():
        return None
    spec = json.loads(spec_path.read_text())
    return {
        "character": str(spec.get("characterId", "")).upper(),
        "cards": [],
        "maxHp": None,
    }


def load_results(directory: Path) -> dict[str, dict | None]:
    results: dict[str, dict | None] = {}
    if not directory.is_dir():
        raise SystemExit(f"result directory does not exist: {directory}")
    for child in sorted(directory.iterdir()):
        if not child.is_dir():
            continue
        full = child / "full-combat.json"
        if full.is_file():
            results[child.name] = json.loads(full.read_text())
        else:
            results[child.name] = None
    return results


def outcome_of(result: dict | None) -> str:
    return "missing" if result is None else str(result.get("outcome", "missing"))


def hp_of(result: dict | None, fallback_max: int | None) -> tuple[int | None, int | None]:
    if result is None:
        return fallback_max, None
    max_hp = result.get("playerMaxHp")
    hp = result.get("playerHp")
    return max_hp, hp


def loss_of(result: dict | None, fallback_max: int | None) -> int:
    max_hp, hp = hp_of(result, fallback_max)
    if max_hp is None:
        return 0
    if outcome_of(result) != "victory" or hp is None:
        return int(max_hp)
    return int(max_hp) - int(hp)


def fmt_delta(value: int) -> str:
    return f"{value:+d}"


def main() -> int:
    args = parse_args()
    new_dir = Path(args.new_dir)
    old_dir = Path(args.old_dir)
    new = load_results(new_dir)
    old = load_results(old_dir)
    roots = sorted(set(new) | set(old))
    if not roots:
        raise SystemExit("no result roots found")
    metadata: dict[str, dict] = {}
    if args.requests_file:
        metadata = read_request_list(Path(args.requests_file))
        # The request list defines the run; stale failure directories from an earlier
        # wider sample must not be counted as extra games.
        roots = [root for root in roots if root in metadata]
    corpora = Path(args.corpora)
    rows = []
    missing_meta = 0
    for root in roots:
        new_result = new.get(root)
        old_result = old.get(root)
        meta = metadata.get(root) or read_quality_metadata(corpora, root)
        if meta is None:
            missing_meta += 1
            meta = {"character": root.split("-")[1] if root.count("-") >= 2 else "UNKNOWN",
                    "cards": [], "maxHp": None}
        fallback_max = (new_result or {}).get("playerMaxHp") or (old_result or {}).get("playerMaxHp") \
            or meta.get("maxHp")
        new_win = outcome_of(new_result) == "victory"
        old_win = outcome_of(old_result) == "victory"
        new_loss = loss_of(new_result, fallback_max)
        old_loss = loss_of(old_result, fallback_max)
        rows.append({
            "root": root,
            "character": meta["character"],
            "cards": meta["cards"],
            "old_outcome": outcome_of(old_result),
            "new_outcome": outcome_of(new_result),
            "old_win": old_win,
            "new_win": new_win,
            "old_hp": (old_result or {}).get("playerHp"),
            "new_hp": (new_result or {}).get("playerHp"),
            "max_hp": fallback_max,
            "old_loss": old_loss,
            "new_loss": new_loss,
            "loss_delta": new_loss - old_loss,
        })

    old_wins = sum(row["old_win"] for row in rows)
    new_wins = sum(row["new_win"] for row in rows)
    old_loss_total = sum(row["old_loss"] for row in rows)
    new_loss_total = sum(row["new_loss"] for row in rows)
    lost_wins = [row for row in rows if row["old_win"] and not row["new_win"]]
    gained_wins = [row for row in rows if row["new_win"] and not row["old_win"]]
    print(f"games={len(rows)} old_wins={old_wins} new_wins={new_wins} "
          f"lost_wins={len(lost_wins)} gained_wins={len(gained_wins)}")
    print(f"hp_loss old={old_loss_total} new={new_loss_total} "
          f"delta={fmt_delta(new_loss_total - old_loss_total)}")
    for title, group in (("lost", lost_wins), ("gained", gained_wins)):
        for row in group:
            print(f"  {title}_win {row['root']} old={row['old_outcome']} "
                  f"new={row['new_outcome']} old_loss={row['old_loss']} new_loss={row['new_loss']}")

    def grouped(key: str):
        groups: dict[str, list[dict]] = defaultdict(list)
        for row in rows:
            values = row[key]
            if isinstance(values, list):
                for value in values:
                    groups[value].append(row)
                if not values:
                    groups["(no deck metadata)"].append(row)
            else:
                groups[values or "UNKNOWN"].append(row)
        return groups

    for title, key in (("character", "character"), ("card", "cards")):
        print(f"per-{title}:")
        for name, group in sorted(grouped(key).items()):
            games = len(group)
            wins_old = sum(row["old_win"] for row in group)
            wins_new = sum(row["new_win"] for row in group)
            loss_old = sum(row["old_loss"] for row in group)
            loss_new = sum(row["new_loss"] for row in group)
            flips = sum(row["old_win"] and not row["new_win"] for row in group)
            print(f"  {name:24s} games={games:3d} wins={wins_old}->{wins_new} "
                  f"lostWins={flips:2d} hpLoss={loss_old}->{loss_new} "
                  f"({fmt_delta(loss_new - loss_old)})")
    if missing_meta:
        print(f"metadata_missing={missing_meta}")

    if args.out:
        output = Path(args.out)
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("w", newline="") as handle:
            writer = csv.writer(handle)
            writer.writerow(["root", "character", "old_outcome", "new_outcome",
                             "old_win", "new_win", "old_hp", "new_hp", "max_hp",
                             "old_loss", "new_loss", "loss_delta", "cards"])
            for row in rows:
                writer.writerow([row["root"], row["character"], row["old_outcome"],
                                 row["new_outcome"], int(row["old_win"]), int(row["new_win"]),
                                 row["old_hp"], row["new_hp"], row["max_hp"],
                                 row["old_loss"], row["new_loss"], row["loss_delta"],
                                 ";".join(row["cards"])])
        print(f"wrote {output} rows={len(rows)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
