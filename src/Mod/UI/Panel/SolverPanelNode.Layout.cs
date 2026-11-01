// Adapted from the responsive layout, drag and resize code of CombatSolver src/UI/SolverOverlay.cs
// at 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and contributors): ApplyResponsiveLayout,
// ApplyPanelBounds, the resize handles and CompletePointerGesture. Geometry is persisted through
// Sts2Solver's SolverSettings on pointer release.
using Godot;

namespace Sts2Solver;

internal sealed partial class SolverPanelNode
{
    private const long ResizeLayoutIntervalMilliseconds = 33;

    private enum ResizeEdge
    {
        Right,
        Bottom,
        BottomRight,
    }

    private Control? _rightHandle;
    private Control? _bottomHandle;
    private Control? _cornerHandle;
    private Vector2 _requestedPosition;
    private Vector2 _panelPosition;
    private Vector2? _customSize;
    private bool _dragging;
    private Vector2 _dragOffset;
    private bool _resizing;
    private ResizeEdge _resizeEdge;
    private Vector2 _resizeStartMouse;
    private Vector2 _resizeStartSize;
    private long _lastResizeLayout;
    private bool _layoutQueued;
    private int _remainingLayoutPasses;

    private static Vector2 DefaultPosition => new(SolverUiTokens.Size.PanelMargin, 110f);

    private void LoadGeometry()
    {
        SolverUiSettings ui = SolverSettings.Current.Ui;
        _requestedPosition = ui.PanelX is { } x && ui.PanelY is { } y ? new Vector2(x, y) : DefaultPosition;
        _panelPosition = _requestedPosition;
        _customSize = ui.PanelWidth is { } width && ui.PanelHeight is { } height
            ? new Vector2(width, height)
            : null;
    }

    private void ResetGeometry()
    {
        CancelPointerGesture();
        _requestedPosition = DefaultPosition;
        _panelPosition = _requestedPosition;
        _customSize = null;
    }

    private void QueueLayout()
    {
        _remainingLayoutPasses = 2;
        if (_layoutQueued)
            return;
        _layoutQueued = true;
        Callable.From(ApplyLayoutDeferred).CallDeferred();
    }

    // Flow rows learn their wrapped height only after a sort pass, so a second pass settles the size.
    private void ApplyLayoutDeferred()
    {
        ApplyLayout();
        if (--_remainingLayoutPasses > 0 && _panel != null)
        {
            Callable.From(ApplyLayoutDeferred).CallDeferred();
            return;
        }
        _layoutQueued = false;
        _remainingLayoutPasses = 0;
    }

    private void ApplyLayout()
    {
        if (_panel == null || !IsInstanceValid(_panel) || !IsInsideTree())
            return;
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        float edge = SolverUiTokens.Size.ResizeEdgeThickness;
        float availableWidth = Math.Max(0f, viewport.X - edge * 2f);
        float availableHeight = Math.Max(0f, viewport.Y - edge * 2f);
        bool routeView = !_collapsed && !(_settingsVisible && !_collapsed);

        if (routeView && _route != null)
        {
            float routeHeight = _customSize == null
                ? Math.Clamp(_route.NaturalRowsHeight, SolverUiTokens.Size.RouteViewportMinHeight,
                    SolverUiTokens.Size.RouteViewportAutoMaxHeight)
                : SolverUiTokens.Size.RouteViewportMinHeight;
            float detailsHeight = _detailsVisible && _details != null
                ? Math.Clamp(_details.NaturalHeight, 60f, SolverUiTokens.Size.DetailsViewportMaxHeight)
                : 0f;
            _route.SetViewportHeight(routeHeight);
            _details?.SetViewportHeight(detailsHeight);
            // Shrink the scroll viewports before the panel would overflow a short screen.
            float overflow = _panel.GetCombinedMinimumSize().Y - availableHeight;
            if (overflow > 0f)
            {
                float routeShrink = Math.Min(overflow, routeHeight - SolverUiTokens.Size.RouteViewportMinHeight);
                _route.SetViewportHeight(routeHeight - routeShrink);
                overflow -= routeShrink;
                if (overflow > 0f && detailsHeight > 60f)
                    _details?.SetViewportHeight(Math.Max(60f, detailsHeight - overflow));
            }
        }

        Vector2 content = _panel.GetCombinedMinimumSize();
        float defaultWidth = Math.Clamp(viewport.X * SolverUiTokens.Size.DefaultWidthShare,
            SolverUiTokens.Size.DefaultMinWidth, SolverUiTokens.Size.DefaultMaxWidth);
        float minimumWidth = Math.Min(Math.Max(SolverUiTokens.Size.MinimumWidth, content.X), availableWidth);
        float width;
        float height;
        if (_collapsed)
        {
            width = Math.Clamp(SolverUiTokens.Size.CollapsedWidth, Math.Min(content.X, availableWidth), availableWidth);
            height = Math.Min(content.Y, availableHeight);
        }
        else
        {
            width = Math.Clamp(_customSize?.X ?? defaultWidth, minimumWidth, availableWidth);
            float minimumHeight = Math.Min(Math.Max(SolverUiTokens.Size.MinimumHeight, content.Y), availableHeight);
            height = _customSize is { } custom
                ? Math.Clamp(custom.Y, minimumHeight, availableHeight)
                : _settingsVisible
                    ? Math.Min(Math.Max(content.Y, 470f), availableHeight)
                    : Math.Min(content.Y, availableHeight);
        }
        ApplyBounds(viewport, width, height);
    }

    private void ApplyBounds(Vector2 viewport, float width, float height)
    {
        if (_panel == null)
            return;
        float edge = SolverUiTokens.Size.ResizeEdgeThickness;
        width = Math.Min(width, Math.Max(0f, viewport.X - edge * 2f));
        height = Math.Min(height, Math.Max(0f, viewport.Y - edge * 2f));
        float maxX = Math.Max(edge, viewport.X - width - edge);
        float maxY = Math.Max(edge, viewport.Y - height - edge);
        _panelPosition = new Vector2(
            Math.Clamp(_requestedPosition.X, edge, maxX),
            Math.Clamp(_requestedPosition.Y, edge, maxY));
        _panel.OffsetLeft = _panelPosition.X;
        _panel.OffsetTop = _panelPosition.Y;
        _panel.OffsetRight = _panelPosition.X + width;
        _panel.OffsetBottom = _panelPosition.Y + height;
        PlaceResizeHandles(width, height);
    }

    private void BuildResizeHandles()
    {
        _rightHandle = CreateResizeHandle("RightResizeHandle", ResizeEdge.Right);
        _bottomHandle = CreateResizeHandle("BottomResizeHandle", ResizeEdge.Bottom);
        _cornerHandle = CreateResizeHandle("CornerResizeHandle", ResizeEdge.BottomRight);
        AddChild(_rightHandle);
        AddChild(_bottomHandle);
        AddChild(_cornerHandle);
    }

    private void FreeResizeHandles()
    {
        foreach (Control? handle in new[] { _rightHandle, _bottomHandle, _cornerHandle })
        {
            if (handle == null || !IsInstanceValid(handle))
                continue;
            RemoveChild(handle);
            handle.QueueFree();
        }
        _rightHandle = null;
        _bottomHandle = null;
        _cornerHandle = null;
    }

    private void SetResizeHandlesVisible(bool visible)
    {
        foreach (Control? handle in new[] { _rightHandle, _bottomHandle, _cornerHandle })
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
                Texture = SolverUiTokens.CreateResizeGripTexture(new Color(SolverUiTokens.Palette.TextMuted, 0.85f)),
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

    private void PlaceResizeHandles(float width, float height)
    {
        float edge = SolverUiTokens.Size.ResizeEdgeThickness;
        float grip = SolverUiTokens.Size.ResizeGripSize;
        float corner = Math.Min(grip, Math.Min(width, height));
        SetHandleBounds(_rightHandle, _panelPosition.X + width - edge, _panelPosition.Y,
            edge, Math.Max(0f, height - grip));
        SetHandleBounds(_bottomHandle, _panelPosition.X, _panelPosition.Y + height - edge,
            Math.Max(0f, width - grip), edge);
        SetHandleBounds(_cornerHandle, _panelPosition.X + width - corner, _panelPosition.Y + height - corner,
            corner, corner);
    }

    private static void SetHandleBounds(Control? handle, float x, float y, float width, float height)
    {
        if (handle == null)
            return;
        handle.OffsetLeft = x;
        handle.OffsetTop = y;
        handle.OffsetRight = x + width;
        handle.OffsetBottom = y + height;
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
                _resizing = false;
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
        _requestedPosition = GetViewport().GetMousePosition() - _dragOffset;
        ApplyBounds(GetViewport().GetVisibleRect().Size, _panel.Size.X, _panel.Size.Y);
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
                _resizeEdge = edge;
                _resizeStartMouse = GetViewport().GetMousePosition();
                _resizeStartSize = new Vector2(
                    Math.Max(SolverUiTokens.Size.MinimumWidth, _panel.Size.X),
                    Math.Max(SolverUiTokens.Size.MinimumHeight, _panel.Size.Y));
                _customSize = _resizeStartSize;
                _lastResizeLayout = System.Environment.TickCount64 - ResizeLayoutIntervalMilliseconds;
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
        if (now - _lastResizeLayout < ResizeLayoutIntervalMilliseconds)
            return;
        _lastResizeLayout = now;
        ResizeToMouse();
    }

    private void ResizeToMouse()
    {
        if (_panel == null)
            return;
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        float edge = SolverUiTokens.Size.ResizeEdgeThickness;
        Vector2 content = _panel.GetCombinedMinimumSize();
        Vector2 minimum = new(
            Math.Max(SolverUiTokens.Size.MinimumWidth, content.X),
            Math.Max(SolverUiTokens.Size.MinimumHeight, content.Y));
        Vector2 maximum = new(
            Math.Max(minimum.X, viewport.X - _panelPosition.X - edge),
            Math.Max(minimum.Y, viewport.Y - _panelPosition.Y - edge));
        Vector2 delta = GetViewport().GetMousePosition() - _resizeStartMouse;
        float width = _resizeEdge is ResizeEdge.Right or ResizeEdge.BottomRight
            ? _resizeStartSize.X + delta.X
            : _resizeStartSize.X;
        float height = _resizeEdge is ResizeEdge.Bottom or ResizeEdge.BottomRight
            ? _resizeStartSize.Y + delta.Y
            : _resizeStartSize.Y;
        _customSize = new Vector2(Math.Clamp(width, minimum.X, maximum.X), Math.Clamp(height, minimum.Y, maximum.Y));
        ApplyLayout();
    }

    /// <summary>Ends a drag or resize and persists the result; called from the header, handles and the input bridge.</summary>
    internal void CompletePointerGesture()
    {
        if (_dragging)
        {
            _dragging = false;
            _requestedPosition = _panelPosition;
            ApplyLayout();
            Vector2 position = _requestedPosition;
            SolverSettings.Update(current => current with
            {
                Ui = current.Ui with { PanelX = position.X, PanelY = position.Y },
            });
            ModLog.Info($"[Sts2Solver] UI_POSITION_SAVED x={position.X:F0} y={position.Y:F0}");
        }
        else if (_resizing)
        {
            ResizeToMouse();
            _resizing = false;
            _requestedPosition = _panelPosition;
            ApplyLayout();
            Vector2 position = _requestedPosition;
            Vector2 size = _customSize ?? throw new InvalidOperationException("缩放结束时没有面板尺寸。");
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
}
