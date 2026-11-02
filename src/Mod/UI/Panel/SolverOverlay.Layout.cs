// Layout, drag and resize ported from CombatSolver src/UI/SolverOverlay.cs
// (ApplyResponsiveLayout through ResetOverlayPosition) at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). Geometry is persisted through
// Sts2Solver's SolverSettings on pointer release.
using Godot;

namespace Sts2Solver;

internal sealed partial class SolverOverlay
{
    private const long ResizeLayoutIntervalMilliseconds = 16;

    private enum ResizeEdge
    {
        Right,
        Bottom,
        BottomRight,
    }

    private Control? _rightResizeHandle;
    private Control? _bottomResizeHandle;
    private Control? _cornerResizeHandle;
    private Vector2 _panelPosition = new(SolverUiTokens.Size.PanelMargin, SolverUiTokens.Size.PanelMargin);
    private Vector2 _requestedPanelPosition = new(SolverUiTokens.Size.PanelMargin, SolverUiTokens.Size.PanelMargin);
    private Vector2? _customPanelSize;
    private bool _dragging;
    private bool _resizing;
    private ResizeEdge _activeResizeEdge = ResizeEdge.BottomRight;
    private Vector2 _dragOffset;
    private Vector2 _resizeStartMousePosition;
    private Vector2 _resizeStartPrimarySize;
    private long _lastResizeLayoutAt;
    private bool _layoutQueued;
    private int _remainingLayoutPasses;

    private void LoadGeometry()
    {
        SolverUiSettings ui = SolverSettings.Current.Ui;
        _requestedPanelPosition = ui.PanelX is { } x && ui.PanelY is { } y
            ? new Vector2(x, y)
            : new Vector2(SolverUiTokens.Size.PanelMargin, SolverUiTokens.Size.PanelMargin);
        _panelPosition = _requestedPanelPosition;
        _customPanelSize = ui.PanelWidth is { } width && ui.PanelHeight is { } height
            ? new Vector2(width, height)
            : null;
    }

    private void ResetOverlayPosition()
    {
        ResetGeometry();
    }

    private void ResetGeometry()
    {
        _requestedPanelPosition = new Vector2(SolverUiTokens.Size.PanelMargin, SolverUiTokens.Size.PanelMargin);
        _panelPosition = _requestedPanelPosition;
        _customPanelSize = null;
        _resizing = false;
        ApplyResponsiveLayout();
    }

    private void QueueLayout()
    {
        _remainingLayoutPasses = 2;
        if (_layoutQueued)
            return;
        _layoutQueued = true;
        Callable.From(ApplyResponsiveLayoutDeferred).CallDeferred();
    }

    private void ApplyResponsiveLayoutDeferred()
    {
        ApplyResponsiveLayout();
        if (--_remainingLayoutPasses > 0 && _panel != null && IsInstanceValid(_panel))
        {
            Callable.From(ApplyResponsiveLayoutDeferred).CallDeferred();
            return;
        }
        _layoutQueued = false;
        _remainingLayoutPasses = 0;
    }

    private void ApplyResponsiveLayout()
    {
        if (_panel == null || !IsInstanceValid(_panel) || !IsInsideTree())
            return;
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        float edge = SolverUiTokens.Size.ResizeEdgeThickness;
        float availableWidth = Math.Max(0f, viewportSize.X - edge * 2f);
        float availableHeight = Math.Max(0f, viewportSize.Y - edge * 2f);
        Vector2 contentMinimum = _panel.GetCombinedMinimumSize();
        float defaultPrimaryWidth = Math.Min(
            SolverUiTokens.Size.ExpandedMaxWidth,
            Math.Max(SolverUiTokens.Size.ExpandedMinWidth, viewportSize.X * 0.58f));
        float minimumWidth = Math.Min(
            Math.Max(SolverSettings.MinimumOverlayWidth, contentMinimum.X),
            availableWidth);
        float width = _collapsed
            ? Math.Clamp(
                SolverUiTokens.Size.CollapsedWidth,
                Math.Min(contentMinimum.X, availableWidth),
                availableWidth)
            : Math.Clamp(_customPanelSize?.X ?? defaultPrimaryWidth, minimumWidth, availableWidth);

        float height;
        if (_collapsed)
        {
            height = Math.Clamp(
                SolverUiTokens.Size.CollapsedHeight,
                Math.Min(contentMinimum.Y, availableHeight),
                availableHeight);
        }
        else if (_customPanelSize is { } customSize)
        {
            float minimumHeight = Math.Min(
                Math.Max(SolverSettings.MinimumOverlayHeight, contentMinimum.Y),
                availableHeight);
            height = Math.Clamp(customSize.Y, minimumHeight, availableHeight);
        }
        else
        {
            float maximumHeight = _settingsVisible ? 540f : SolverUiTokens.Size.ExpandedMaxHeight;
            height = Math.Min(contentMinimum.Y, Math.Min(maximumHeight, availableHeight));
        }

        ApplyPanelBounds(viewportSize, width, height);
    }

    private void ApplyPanelBounds(Vector2 viewportSize, float width, float height)
    {
        if (_panel == null)
            return;
        float edge = SolverUiTokens.Size.ResizeEdgeThickness;
        width = Math.Min(width, Math.Max(0f, viewportSize.X - edge * 2f));
        height = Math.Min(height, Math.Max(0f, viewportSize.Y - edge * 2f));
        float maxX = Math.Max(edge, viewportSize.X - width - edge);
        float maxY = Math.Max(edge, viewportSize.Y - height - edge);
        _panelPosition = new Vector2(
            Math.Clamp(_requestedPanelPosition.X, edge, maxX),
            Math.Clamp(_requestedPanelPosition.Y, edge, maxY));
        _panel.OffsetLeft = _panelPosition.X;
        _panel.OffsetTop = _panelPosition.Y;
        _panel.OffsetRight = _panelPosition.X + width;
        _panel.OffsetBottom = _panelPosition.Y + height;
        ApplyResizeHandleBounds(width, height);
    }

    private void ApplyResizeHandleBounds(float panelWidth, float panelHeight)
    {
        bool visible = !_collapsed;
        float edge = SolverUiTokens.Size.ResizeEdgeThickness;
        float grip = SolverUiTokens.Size.ResizeGripSize;
        float cornerSize = Math.Min(grip, Math.Min(panelWidth, panelHeight));
        SetResizeHandleBounds(
            _rightResizeHandle,
            _panelPosition.X + panelWidth - edge,
            _panelPosition.Y,
            edge,
            Math.Max(0f, panelHeight - grip),
            visible);
        SetResizeHandleBounds(
            _bottomResizeHandle,
            _panelPosition.X,
            _panelPosition.Y + panelHeight - edge,
            Math.Max(0f, panelWidth - grip),
            edge,
            visible);
        SetResizeHandleBounds(
            _cornerResizeHandle,
            _panelPosition.X + panelWidth - cornerSize,
            _panelPosition.Y + panelHeight - cornerSize,
            cornerSize,
            cornerSize,
            visible);
    }

    private static void SetResizeHandleBounds(
        Control? handle,
        float x,
        float y,
        float width,
        float height,
        bool visible)
    {
        if (handle == null)
            return;
        handle.Visible = visible;
        handle.OffsetLeft = x;
        handle.OffsetTop = y;
        handle.OffsetRight = x + width;
        handle.OffsetBottom = y + height;
    }

    private void BuildResizeHandles()
    {
        _rightResizeHandle = CreateResizeHandle("RightResizeHandle", ResizeEdge.Right);
        _bottomResizeHandle = CreateResizeHandle("BottomResizeHandle", ResizeEdge.Bottom);
        _cornerResizeHandle = CreateResizeHandle("CornerResizeHandle", ResizeEdge.BottomRight);
        AddChild(_rightResizeHandle);
        AddChild(_bottomResizeHandle);
        AddChild(_cornerResizeHandle);
    }

    private void FreeResizeHandles()
    {
        foreach (Control? handle in new[] { _rightResizeHandle, _bottomResizeHandle, _cornerResizeHandle })
        {
            if (handle == null || !IsInstanceValid(handle))
                continue;
            RemoveChild(handle);
            handle.QueueFree();
        }
        _rightResizeHandle = null;
        _bottomResizeHandle = null;
        _cornerResizeHandle = null;
    }

    private void SetResizeHandlesVisible(bool visible)
    {
        foreach (Control? handle in new[] { _rightResizeHandle, _bottomResizeHandle, _cornerResizeHandle })
        {
            if (handle != null)
                handle.Visible = visible;
        }
    }

    private Control CreateResizeHandle(string name, ResizeEdge edge)
    {
        Control handle = edge == ResizeEdge.BottomRight
            ? new TextureRect
            {
                Texture = SolverUiTokens.CreateResizeGripTexture(
                    new Color(SolverUiTokens.Palette.TextMuted, 0.88f)),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            }
            : new Control();
        handle.Name = name;
        handle.MouseFilter = Control.MouseFilterEnum.Stop;
        handle.ZIndex = 1;
        handle.MouseDefaultCursorShape = edge switch
        {
            ResizeEdge.Right => Control.CursorShape.Hsize,
            ResizeEdge.Bottom => Control.CursorShape.Vsize,
            _ => Control.CursorShape.Fdiagsize,
        };
        handle.GuiInput += inputEvent => OnResizeHandleGuiInput(inputEvent, edge);
        return handle;
    }

    private void OnHeaderGuiInput(InputEvent inputEvent)
    {
        if (_panel == null)
            return;
        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed)
            {
                _dragging = true;
                _dragOffset = GetViewport().GetMousePosition() - _panelPosition;
            }
            else
            {
                CompletePointerGesture();
            }
            return;
        }
        if (!_dragging || inputEvent is not InputEventMouseMotion)
            return;
        _requestedPanelPosition = GetViewport().GetMousePosition() - _dragOffset;
        ApplyPanelBounds(GetViewport().GetVisibleRect().Size, _panel.Size.X, _panel.Size.Y);
    }

    private void OnResizeHandleGuiInput(InputEvent inputEvent, ResizeEdge edge)
    {
        if (_panel == null || _collapsed)
            return;
        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed)
            {
                _resizing = true;
                _dragging = false;
                _activeResizeEdge = edge;
                _resizeStartMousePosition = GetViewport().GetMousePosition();
                _resizeStartPrimarySize = new Vector2(
                    Math.Max(SolverSettings.MinimumOverlayWidth, _panel.Size.X),
                    Math.Max(SolverSettings.MinimumOverlayHeight, _panel.Size.Y));
                _customPanelSize = _resizeStartPrimarySize;
                _lastResizeLayoutAt = System.Environment.TickCount64 - ResizeLayoutIntervalMilliseconds;
            }
            else
            {
                CompletePointerGesture();
            }
            return;
        }
        if (!_resizing || inputEvent is not InputEventMouseMotion)
            return;
        long now = System.Environment.TickCount64;
        if (now - _lastResizeLayoutAt < ResizeLayoutIntervalMilliseconds)
            return;
        _lastResizeLayoutAt = now;
        ResizeToMousePosition();
    }

    internal void CompletePointerGesture()
    {
        if (_dragging)
        {
            _dragging = false;
            _requestedPanelPosition = _panelPosition;
            ApplyResponsiveLayout();
            Vector2 position = _requestedPanelPosition;
            SolverSettings.Update(current => current with
            {
                Ui = current.Ui with { PanelX = position.X, PanelY = position.Y },
            });
            ModLog.Info($"[Sts2Solver] UI_POSITION_SAVED x={position.X:F0} y={position.Y:F0}");
        }
        else if (_resizing)
        {
            ResizeToMousePosition();
            _resizing = false;
            _requestedPanelPosition = _panelPosition;
            ApplyResponsiveLayout();
            Vector2 position = _requestedPanelPosition;
            Vector2 size = _customPanelSize ?? throw new InvalidOperationException("缩放结束时没有面板尺寸。");
            SolverSettings.Update(current => current with
            {
                Ui = current.Ui with
                {
                    PanelX = position.X,
                    PanelY = position.Y,
                    PanelWidth = size.X,
                    PanelHeight = size.Y,
                },
            });
            ModLog.Info(
                $"[Sts2Solver] UI_SIZE_SAVED x={position.X:F0} y={position.Y:F0} w={size.X:F0} h={size.Y:F0}");
        }
    }

    private void CancelPointerGesture()
    {
        _dragging = false;
        _resizing = false;
    }

    private void ResizeToMousePosition()
    {
        if (_panel == null)
            return;
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        float edgeMargin = SolverUiTokens.Size.ResizeEdgeThickness;
        float maximumWidth = Math.Max(
            SolverSettings.MinimumOverlayWidth,
            viewportSize.X - _panelPosition.X - edgeMargin);
        float maximumHeight = Math.Max(
            SolverSettings.MinimumOverlayHeight,
            viewportSize.Y - _panelPosition.Y - edgeMargin);
        Vector2 contentMinimum = _panel.GetCombinedMinimumSize();
        Vector2 minimumSize = new(
            Math.Max(SolverSettings.MinimumOverlayWidth, contentMinimum.X),
            Math.Max(SolverSettings.MinimumOverlayHeight, contentMinimum.Y));
        Vector2 delta = GetViewport().GetMousePosition() - _resizeStartMousePosition;
        _customPanelSize = ResizePanelSize(
            _activeResizeEdge,
            _resizeStartPrimarySize,
            delta,
            minimumSize,
            new Vector2(maximumWidth, maximumHeight));
        ApplyResponsiveLayout();
    }

    private static Vector2 ResizePanelSize(
        ResizeEdge edge,
        Vector2 startSize,
        Vector2 delta,
        Vector2 minimumSize,
        Vector2 maximumSize)
    {
        float width = edge is ResizeEdge.Right or ResizeEdge.BottomRight
            ? startSize.X + delta.X
            : startSize.X;
        float height = edge is ResizeEdge.Bottom or ResizeEdge.BottomRight
            ? startSize.Y + delta.Y
            : startSize.Y;
        return new Vector2(
            Math.Clamp(width, minimumSize.X, Math.Max(minimumSize.X, maximumSize.X)),
            Math.Clamp(height, minimumSize.Y, Math.Max(minimumSize.Y, maximumSize.Y)));
    }
}
