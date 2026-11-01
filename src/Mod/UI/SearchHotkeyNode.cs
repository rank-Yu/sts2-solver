using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Solver;

/// <summary>
/// The ordinary-game trigger: F8 toggles whole-combat automation. The panel's full-auto button goes
/// through <see cref="CombatUiCommands"/>; this node only owns the key edge.
/// </summary>
internal sealed partial class SearchHotkeyNode : Node
{
    public override void _Ready() => SetProcessUnhandledInput(true);

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key
            || key.Keycode != Key.F8)
        {
            return;
        }
        NGame? host = NGame.Instance;
        if (host == null)
            return;
        ModLog.Info("[Sts2Solver] HOTKEY_AUTO_TOGGLE key=F8");
        AutoCombatSession.Toggle(host);
    }
}
