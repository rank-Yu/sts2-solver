# STS2 Solver

[中文](README.md)

A combat solver Mod for Slay the Spire 2, single player. When a fight starts it reads the current combat and the real random-number state, searches a card route for the whole fight, then plays cards and potions through the game's own actions and checks the result of every action.

In Slay the Spire 2, quitting and re-entering restarts the fight with the same outcomes, so in-combat randomness is fixed; the solver relies on this to predict each step. It never looks at anything revealed only after the fight ends, such as rewards.

The Mod handles combat only; the map, events, shops and everything else stay with the player.

Its search strategy and position scoring are ported from [CombatSolver](https://github.com/Torch1230/CombatSolver); the simulation kernel is rewritten: the combat state is one compact block of memory, copying a branch is a single memory copy, and the search no longer clones game objects.

## Compared with CombatSolver

The same 108 combats, both solvers on Medium, full auto to the end of the fight:

| Character | Combats | Wins (STS2 Solver / CombatSolver) | Avg HP lost, combats both won |
|---|---:|---:|---:|
| Ironclad | 21 | 20 / 20 | 19.6 / 22.9 |
| Silent | 24 | 17 / 16 | 9.4 / 9.7 |
| Defect | 21 | 20 / 18 | 22.7 / 25.1 |
| Necrobinder | 21 | 18 / 18 | 19.9 / 22.4 |
| Regent | 21 | 20 / 17 | 20.6 / 22.1 |
| **Total** | **108** | **95 / 89** | **18.7 / 20.7** |

| Encounter | Combats | Wins (STS2 Solver / CombatSolver) | Avg HP lost, combats both won |
|---|---:|---:|---:|
| Normal | 40 | 40 / 40 | 11.1 / 12.2 |
| Elite | 40 | 34 / 31 | 23.9 / 24.8 |
| Boss | 28 | 21 / 18 | 26.5 / 32.6 |

STS2 Solver wins every combat CombatSolver wins, plus 6 more; both lose the same 13. Of the 89 combats both win, STS2 Solver loses less HP in 27, CombatSolver in 10, and 52 are equal.

Opening search time (both solvers almost always search once at the start and then follow that route):

| | Median | Mean | Max |
|---|---:|---:|---:|
| STS2 Solver | 2.2 s | 3.4 s | 44.9 s |
| CombatSolver | 8.5 s | 13.4 s | 120.4 s |

STS2 Solver searches faster in 93 of the 105 generated decks.

Test conditions:

- Combats: 21 randomly generated decks per character (Ascension 10; 8 normal, 8 elite, 5 boss fights), plus 3 boss fights with fixed Silent decks.
- CombatSolver: main after 0.50.1 (`5c773caa`), full auto in the real game running headless, Smart potions. Its test tool cannot set gold, so the 3 fixed decks ran with 99 gold instead of 35.
- STS2 Solver: full auto in its offline kernel with the production Medium budget.
- Time: opening search only, on the 105 generated decks. Both ran offline on the same machine (Intel Core Ultra 9 285K) without the game's graphics, with search parallelism 4; CombatSolver used its own offline host running its production search.

## Installation

Requires game v0.111.0 and RitsuLib 0.6.0 or later.

1. Install RitsuLib separately.
2. Put the `Sts2Solver/` folder from the release package into the game's `mods/` folder.

It can be installed together with CombatSolver. Both can search, but let only one of them execute each turn: two solvers playing cards or picking cards at the same time get in each other's way. With CombatSolver present, STS2 Solver takes over the turn-start card choice screen only in Full auto.

## Usage

- During a fight the solver panel appears at the top left: search progress, the route for the whole fight, and the step being executed. Drag the title bar to move it, drag an edge to resize, and collapse it when you don't need it.
- The title bar has three modes: in **Manual** you press both buttons; in **Auto-search** it searches every turn and you press **Play turn**; in **Full auto** it searches and plays by itself. A fresh install starts in Auto-search; the mode is kept across fights and restarts.
- The bottom bar has the search button (Search / Search again / Stop search) on the left and the execute button (Play turn / Stop execution) on the right. Stopping affects only the current turn; the next turn follows the mode again.
- If a result differs from the prediction or an action cannot be performed, execution stops at once without searching again; press **Search again** to continue. When Full auto pauses this way, click **Full auto** in the title bar to resume.
- Unsupported content (for example cards added by other Mods) is rejected explicitly instead of being played blindly.
- **Settings** in the title bar holds play speed, search tier (Medium by default), search parallelism, search memory limit and overlay opacity; **Potions** and **Growth** open per-potion and growth strategies; in fights with a thief, the banner lets you choose whether to recover or let go of stolen items. Changes apply from the next search.
- See [docs/mod.md](docs/mod.md) (Chinese) for details. Status and errors go to the game log with the prefix `[Sts2Solver]`. The panel follows the game language: Chinese for Simplified or Traditional Chinese, English otherwise.

## Building

Requires the .NET 9 SDK and a local copy of the game files. Copy `src/Mod/local.props.example` to `src/Mod/local.props`, fill in the game and RitsuLib paths, then run:

```bash
dotnet build src/Mod -c Release
```

The package is written to `artifacts/mod/Sts2Solver/`. Test builds and more details are in [docs/mod.md](docs/mod.md); developer documentation starts at [docs/README.md](docs/README.md) (both in Chinese).

## Credits and license

Released under the [MIT License](LICENSE). Parts of the code are ported from [CombatSolver](https://github.com/Torch1230/CombatSolver) (marked in file headers), with reference to [Random Foreseer](https://github.com/hotwords123/StS2.RandomForeseer). Both projects are MIT licensed; their copyright notices are kept as required:

- CombatSolver: Copyright (c) 2026 Torch1230 and contributors
- Random Foreseer: Copyright (c) 2026 hotwords123
