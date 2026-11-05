#!/usr/bin/env python3
"""Sample sts2-sim exported TestHost requests by deck card.

The script scans a sts2-sim export tree for request JSON files (the same DTO as
`tests/Differential`'s `OfflineRequest`), keeps games whose deck contains one of
the requested card ids, deduplicates them by `scenarioId`, and writes a
`--requests-file` list for:

    Differential --offline-combat --requests-file <out> ...

For every card it selects at most `--per-card` games (default 25). Selection
round-robins across acts and prefers previously unseen encounters, so a small
sample still covers different acts and encounter kinds. The output is one
`<scenarioId>=<absolute request path>` line per selected game; the name becomes
the offline result directory.

Example:

    python3 tools/testing/sample-sts2-sim.py \
      --cards CRUELTY INFLAME DEMON_FORM RUPTURE STAMPEDE INFERNO \
      --per-card 12 --out .local/pw1-damage-powers.txt

The default corpus is `~/ss/solver/.local/sts2-sim-corpus`; pass `--corpus`
to scan a different export tree. Only `runCards` and card injections marked
`treatAsDeckCard` count as deck contents.
"""

import argparse
import json
import os
import sys
from collections import defaultdict
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--cards", nargs="+", required=True,
                        help="card ids whose games should be sampled")
    parser.add_argument("--per-card", type=int, default=25,
                        help="maximum games per card (default: 25)")
    parser.add_argument("--corpus", default=os.path.expanduser(
                        "~/ss/solver/.local/sts2-sim-corpus"),
                        help="sts2-sim export tree (default: ~/ss/solver/.local/sts2-sim-corpus)")
    parser.add_argument("--out", required=True,
                        help="output request list for Differential --requests-file")
    parser.add_argument("--max-total", type=int, default=0,
                        help="fail if the deduplicated selection exceeds this total (0 = no limit)")
    return parser.parse_args()


def deck_cards(request: dict) -> set[str]:
    cards: set[str] = set()
    for entry in request.get("runCards") or []:
        card_id = entry.get("cardId")
        if isinstance(card_id, str):
            cards.add(card_id)
    for entry in request.get("cards") or []:
        if not entry.get("treatAsDeckCard"):
            continue
        card_id = entry.get("cardId")
        if isinstance(card_id, str):
            cards.add(card_id)
    return cards


def load_requests(corpus: Path) -> tuple[dict[str, dict], int, int]:
    by_scenario: dict[str, dict] = {}
    scanned = 0
    unreadable = 0
    for path in sorted(corpus.rglob("*.json")):
        try:
            text = path.read_text()
        except FileNotFoundError:
            unreadable += 1
            continue
        except OSError as error:
            raise SystemExit(f"cannot read {path}: {error}")
        if '"runCards"' not in text and '"cards"' not in text:
            continue
        try:
            request = json.loads(text)
        except json.JSONDecodeError as error:
            raise SystemExit(f"invalid JSON in {path}: {error}")
        if not isinstance(request, dict) or "scenarioId" not in request:
            continue
        scenario = request["scenarioId"]
        if not isinstance(scenario, str) or not scenario:
            raise SystemExit(f"request without a usable scenarioId: {path}")
        scanned += 1
        entry = {
            "scenario": scenario,
            "path": path.resolve(),
            "character": str(request.get("characterId", "")).upper(),
            "act": request.get("actIndexForTest", 0),
            "encounter": str(request.get("encounterId", "")),
            "cards": deck_cards(request),
        }
        current = by_scenario.get(scenario)
        # Duplicate scenario ids appear in export/selected/rerun trees. Prefer the
        # shortest path, then the lexicographically first path, deterministically.
        if current is None or (len(str(entry["path"])), str(entry["path"])) < (
                len(str(current["path"])), str(current["path"])):
            by_scenario[scenario] = entry
    return by_scenario, scanned, unreadable


def sample_card(candidates: list[dict], limit: int) -> list[dict]:
    buckets: dict[int, list[dict]] = defaultdict(list)
    for candidate in candidates:
        buckets[int(candidate["act"])].append(candidate)
    for act in buckets:
        buckets[act].sort(key=lambda item: (item["encounter"], item["scenario"]))
    selected: list[dict] = []
    used_encounters: set[str] = set()
    # Round 1: one unseen encounter per act per round, keeping acts balanced.
    progressed = True
    while len(selected) < limit and progressed:
        progressed = False
        for act in sorted(buckets):
            bucket = buckets[act]
            pick_index = next((index for index, item in enumerate(bucket)
                               if item["encounter"] not in used_encounters), None)
            if pick_index is None:
                continue
            item = bucket.pop(pick_index)
            selected.append(item)
            used_encounters.add(item["encounter"])
            progressed = True
            if len(selected) == limit:
                break
    # Round 2: fill the remaining slots across acts.
    progressed = True
    while len(selected) < limit and progressed:
        progressed = False
        for act in sorted(buckets):
            bucket = buckets[act]
            if not bucket:
                continue
            item = bucket.pop(0)
            selected.append(item)
            progressed = True
            if len(selected) == limit:
                break
    return selected


def main() -> int:
    args = parse_args()
    if args.per_card <= 0:
        raise SystemExit("--per-card must be positive")
    corpus = Path(args.corpus).expanduser()
    if not corpus.is_dir():
        raise SystemExit(f"corpus directory does not exist: {corpus}")
    requested = [card.upper() for card in args.cards]
    by_scenario, scanned, unreadable = load_requests(corpus)
    chosen: dict[str, dict] = {}
    report: list[str] = []
    for card in requested:
        candidates = [entry for entry in by_scenario.values() if card in entry["cards"]]
        picked = sample_card(candidates, args.per_card)
        for entry in picked:
            chosen.setdefault(entry["scenario"], entry)
        acts = sorted({int(entry["act"]) for entry in picked})
        encounters = len({entry["encounter"] for entry in picked})
        report.append(f"# {card}: candidates={len(candidates)} picked={len(picked)} "
                      f"acts={acts} encounters={encounters}")
    if args.max_total and len(chosen) > args.max_total:
        raise SystemExit(f"selection has {len(chosen)} games, over --max-total {args.max_total}")
    output = Path(args.out)
    output.parent.mkdir(parents=True, exist_ok=True)
    lines = [
        "# generated by tools/testing/sample-sts2-sim.py",
        f"# corpus={corpus} unreadable={unreadable}",
        f"# cards={' '.join(requested)} per_card={args.per_card} unique_games={len(chosen)}",
        *report,
    ]
    for scenario in sorted(chosen):
        lines.append(f"{scenario}={chosen[scenario]['path']}")
    output.write_text("\n".join(lines) + "\n")
    print(f"sampled scanned={scanned} unreadable={unreadable} "
          f"unique_scenarios={len(by_scenario)} selected={len(chosen)} out={output}")
    for line in report:
        print(line[2:])
    return 0


if __name__ == "__main__":
    sys.exit(main())
