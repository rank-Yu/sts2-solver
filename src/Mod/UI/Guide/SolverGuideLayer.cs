// In-game guide drawn over the combat scene. It reads CombatUiSnapshot.Guide (the next route step
// the live state has reached) and resolves the step to the native nodes on screen: the hand card or
// potion to use, the creature to target, the end-turn button, and the cards to pick on a hand or grid
// selection. Read-only: it never clicks, plays or selects anything, and it takes no mouse input.
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using NativeCreature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;

namespace Sts2Solver;

/// <summary>
/// Canvas layer under the solver panel (layer 110) that draws the guide for the current step.
/// Main thread only.
/// </summary>
internal sealed partial class SolverGuideLayer : CanvasLayer
{
    internal const string NodeName = "Sts2SolverGuide";

    private readonly SolverGuideCanvas _canvas = new() { Name = "GuideCanvas" };
    private readonly PanelContainer _caption = new() { Name = "GuideCaption", Visible = false };
    private Label? _captionLabel;
    private (GuideStepUi Step, long Choice, string Language)? _captionKey;
    private string? _captionText;

    public override void _Ready()
    {
        Name = NodeName;
        Layer = 110;
        _canvas.MouseFilter = Control.MouseFilterEnum.Ignore;
        _canvas.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_canvas);
        _caption.MouseFilter = Control.MouseFilterEnum.Ignore;
        _caption.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Background,
            SolverUiTokens.Palette.Choice,
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Sm,
            borderWidth: 1));
        AddChild(_caption);
    }

    public override void _Process(double delta)
    {
        SolverGuideFrame? frame = BuildFrame();
        if (frame != null || _canvas.Frame != null)
        {
            _canvas.Frame = frame;
            _canvas.QueueRedraw();
        }
        if (frame == null)
        {
            _caption.Visible = false;
            return;
        }
        ShowCaption(frame.Value);
    }

    private SolverGuideFrame? BuildFrame()
    {
        if (!SolverSettings.Current.Ui.ShowGuide || MegaCrit.Sts2.Core.Localization.LocManager.Instance == null)
            return null;
        CombatManager manager = CombatManager.Instance;
        if (manager == null || !manager.IsInProgress || CombatSessionController.IsMultiplayerSession)
            return null;
        CombatState? state = manager.DebugOnlyGetState();
        if (state == null || state.Players.Count != 1 || LocalContext.GetMe(state) is not { } player)
            return null;
        GuideStepUi? step = CombatUiStore.Snapshot.Guide;
        if (step == null || step.Turn != player.PlayerCombatState?.TurnNumber)
            return null;
        if (NCombatRoom.Instance is not { } room || !room.IsInsideTree() || room.Ui is not { } ui)
            return null;
        if (NCapstoneContainer.Instance?.InUse == true || NModalContainer.Instance?.OpenModal != null)
            return null;

        IReadOnlyList<CardModel>? selected = CombatSessionController.GuideChoiceCards(step,
            out NativeChoiceSurfaceKind kind, out long sequence);
        if (selected != null)
        {
            return NativeChoiceSurface.GetState(kind, out Node? surface)
                    == NativeChoiceSurfaceState.ExpectedVisible
                ? ChoiceFrame(step, surface!, selected, sequence)
                : null;
        }
        if (state.CurrentSide != CombatSide.Player || ui.Hand.IsInCardSelection
            || NOverlayStack.Instance?.Peek() != null)
        {
            return null;
        }
        return step.Action.Kind switch
        {
            "play_card" => CardFrame(step, state, player, room, ui.Hand),
            "use_potion" => PotionFrame(step, state, player, room),
            "end_turn" => EndTurnFrame(step, ui),
            _ => null,
        };
    }

    private SolverGuideFrame? ChoiceFrame(GuideStepUi step, Node surface,
        IReadOnlyList<CardModel> selected, long sequence)
    {
        List<SolverGuideRect> marks = [];
        foreach (NCardHolder holder in Descendants<NCardHolder>(surface))
        {
            if (holder.CardModel is { } card
                && selected.Contains(card, ReferenceEqualityComparer.Instance)
                && SolverGuideRect.Of(holder.Hitbox) is { } rect)
            {
                marks.Add(rect);
            }
        }
        if (selected.Count > 0 && marks.Count == 0)
            return null;
        Vector2 anchor = marks.Count > 0
            ? marks.Aggregate(marks[0].Top, (top, rect) => rect.Top.Y < top.Y ? rect.Top : top)
            : _canvas.GetViewportRect().GetCenter();
        return new SolverGuideFrame(SolverGuideKind.Choice, null, null, marks, null, anchor,
            CaptionText(step, () => selected.Count == 0
                ? SolverText.Get("ui.skip_choice")
                : SolverText.Format("ui.guide_select_cards", string.Join(SolverText.Get("text.list_separator"),
                    selected.Select(card => CombatDisplayNames.CardTitle(card.Id.Entry,
                        card.CurrentUpgradeLevel)))), sequence));
    }

    private SolverGuideFrame? CardFrame(GuideStepUi step, CombatState state, Player player,
        NCombatRoom room, NPlayerHand hand)
    {
        IReadOnlyList<CardModel>? cards = player.PlayerCombatState?.Hand.Cards;
        if (cards == null || step.HandIndex < 0 || step.HandIndex >= cards.Count)
            return null;
        CardModel card = cards[step.HandIndex];
        if (card.Id.Entry != step.Action.CardId || card.CurrentUpgradeLevel != step.Action.Upgrade)
            return null;
        // The card is being dragged or aimed: the native arrow takes over, the target stays marked.
        bool playing = hand.InCardPlay || NTargetManager.Instance?.IsInSelection == true;
        SolverGuideRect? source = playing || hand.GetCardHolder(card) is not { } holder
            ? null
            : SolverGuideRect.Of(holder.Hitbox);
        SolverGuideRect? target = TargetRect(step.Action, state, player, room);
        if (source == null && target == null)
            return null;
        SolverGuideKind kind = step.Action.CardType switch
        {
            "attack" => SolverGuideKind.Attack,
            "power" => SolverGuideKind.Power,
            _ => SolverGuideKind.Skill,
        };
        Vector2 anchor = source?.Top ?? target!.Value.Top;
        Vector2? dropPoint = target == null && source != null ? PlayDropPoint() : null;
        return new SolverGuideFrame(kind, source, target, [], dropPoint, anchor,
            CaptionText(step, () =>
            {
                string name = CombatDisplayNames.CardTitle(card.Id.Entry, card.CurrentUpgradeLevel);
                return WithTarget(SolverText.Format("ui.guide_play_card", name),
                    TargetName(step.Action, state));
            }));
    }

    private SolverGuideFrame? PotionFrame(GuideStepUi step, CombatState state, Player player,
        NCombatRoom room)
    {
        if (step.PotionSlot < 0 || NRun.Instance?.GlobalUi?.TopBar?.PotionContainer is not { } container)
            return null;
        NPotionHolder? holder = container.Holders
            .FirstOrDefault(candidate => candidate.IsVisibleInTree()
                && candidate.Potion?.Model is { } model
                && ReferenceEquals(model, player.GetPotionAtSlotIndex(step.PotionSlot)));
        if (holder == null || holder.Potion?.Model?.Id.Entry != step.Action.PotionId)
            return null;
        SolverGuideRect? source = SolverGuideRect.Of(holder);
        SolverGuideRect? target = step.Action.TargetKind == "enemy"
            ? TargetRect(step.Action, state, player, room)
            : null;
        Vector2 below = source == null ? Vector2.Zero : new Vector2(source.Value.Center.X, source.Value.Bottom.Y + 64);
        return source == null ? null
            : new SolverGuideFrame(SolverGuideKind.Potion, source, target, [], null, below,
                CaptionText(step, () =>
                {
                    string name = CombatDisplayNames.PotionTitle(step.Action.PotionId ?? "");
                    string? targetName = step.Action.TargetKind == "enemy"
                        ? TargetName(step.Action, state)
                        : null;
                    return WithTarget(SolverText.Format("ui.guide_use_potion", name), targetName);
                }), CaptionBelow: true);
    }

    private SolverGuideFrame? EndTurnFrame(GuideStepUi step, NCombatUi ui)
    {
        if (ui.EndTurnButton is not { } button || !button.IsVisibleInTree()
            || SolverGuideRect.Of(button) is not { } rect)
        {
            return null;
        }
        return new SolverGuideFrame(SolverGuideKind.EndTurn, rect, null, [], null, rect.Top,
            CaptionText(step, static () => SolverText.Get("ui.end_turn")));
    }

    private static SolverGuideRect? TargetRect(RouteActionUi action, CombatState state, Player player,
        NCombatRoom room)
    {
        NativeCreature? creature = action.TargetKind switch
        {
            "enemy" when action.TargetCombatId is { } id => state.GetCreature(id),
            "player" => player.Creature,
            "osty" => player.Osty,
            _ => null,
        };
        if (creature == null || !creature.IsAlive || room.GetCreatureNode(creature) is not { } node)
            return null;
        return SolverGuideRect.Of(node.Hitbox);
    }

    private static string? TargetName(RouteActionUi action, CombatState state)
    {
        if (action.TargetKind is not ("enemy" or "player" or "osty"))
            return null;
        CombatDisplayNames names = CombatDisplayNames.Capture(state);
        return action.TargetKind switch
        {
            "enemy" => names.Enemy(action.TargetCombatId ?? 0, action.TargetMonsterId ?? "",
                action.TargetOrdinal),
            "player" => names.PlayerName,
            _ => names.OstyName,
        };
    }

    /// <summary>Where an untargeted card is released: above the hand, between the two sides.</summary>
    private Vector2 PlayDropPoint()
    {
        Rect2 view = _canvas.GetViewportRect();
        return new Vector2(view.GetCenter().X, view.Position.Y + view.Size.Y * 0.45f);
    }

    private static string WithTarget(string action, string? target)
        => target == null ? action : $"{action} → {target}";

    private string CaptionText(GuideStepUi step, Func<string> body, long choice = 0)
    {
        var key = (step, choice, SolverText.Language);
        if (_captionKey == key && _captionText != null)
            return _captionText;
        _captionKey = key;
        _captionText = SolverText.Format("ui.guide_step", step.TurnStep, step.TurnStepCount) + "  " + body();
        return _captionText;
    }

    private void ShowCaption(SolverGuideFrame frame)
    {
        if (_captionLabel == null)
        {
            _captionLabel = SolverUiTokens.CreateLabel(string.Empty, SolverUiTokens.Type.Body,
                SolverUiTokens.Palette.TextPrimary);
            _captionLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
            _caption.AddChild(_captionLabel);
        }
        if (_captionLabel.Text != frame.Caption)
        {
            _captionLabel.Text = frame.Caption;
            _caption.ResetSize();
        }
        _caption.Visible = true;
        Rect2 view = _canvas.GetViewportRect();
        Vector2 size = _caption.GetCombinedMinimumSize();
        Vector2 position = frame.CaptionBelow
            ? new Vector2(frame.CaptionAnchor.X - size.X / 2, frame.CaptionAnchor.Y)
            : new Vector2(frame.CaptionAnchor.X - size.X / 2, frame.CaptionAnchor.Y - size.Y - 28);
        const float margin = 16;
        position.X = Mathf.Clamp(position.X, view.Position.X + margin,
            Math.Max(view.Position.X + margin, view.End.X - size.X - margin));
        position.Y = Mathf.Clamp(position.Y, view.Position.Y + margin,
            Math.Max(view.Position.Y + margin, view.End.Y - size.Y - margin));
        _caption.Position = position;
    }

    private static IEnumerable<T> Descendants<T>(Node node) where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T match)
                yield return match;
            foreach (T descendant in Descendants<T>(child))
                yield return descendant;
        }
    }
}
