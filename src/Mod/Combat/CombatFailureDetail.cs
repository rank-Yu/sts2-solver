// Machine-readable detail envelopes shared by the controller and the search runner. The panel's
// reason classifier reads these stable tokens; exception messages stay diagnostic only.
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver;

internal static class CombatFailureDetail
{
    internal static string Capacity(CapacityException error, int? actual = null)
        => actual is { } count
            ? $"region={error.Region} capacity={error.Capacity} actual={count} {error.Message}"
            : $"region={error.Region} capacity={error.Capacity} {error.Message}";
}
