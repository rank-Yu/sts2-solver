// Shared by tests/Differential (message-loop pumping, no Godot) and the in-game Mod (native NGame
// startup and frame waits). ScenarioBuilder owns all fixture construction, so both hosts run the
// exact same injection sequence and differ only in how the run and the main loop are driven.
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Solver.TestHost;

internal interface ICombatHost
{
    /// <summary>Creates a new single-player run, finalizes relics, launches it and enters act 0.</summary>
    Task<RunState> StartRunAsync(CharacterModel character, int ascension, string seed);

    /// <summary>Waits until the native combat reaches the local player's Play phase.</summary>
    Task<CombatState> WaitForPlayableCombatAsync();

    /// <summary>
    /// Waits until the local player reaches Play, or, for a visible take-over fixture, until the
    /// setup page is open with the setup transaction still running. The offline host never takes
    /// over setup pages, so its default is the ordinary Play wait.
    /// </summary>
    Task<CombatState> WaitForTurnSetupPageAsync() => WaitForPlayableCombatAsync();
}
