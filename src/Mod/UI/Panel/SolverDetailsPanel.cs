// The details panel replaces CombatSolver's _detailsPanel rich text in src/UI/SolverOverlay.cs at
// 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and contributors) with Sts2Solver's own
// search and session numbers; only the container style is carried over.
using System.Text;
using Godot;

namespace Sts2Solver;

/// <summary>Search, member and session diagnostics rendered from the snapshot as one rich text.</summary>
internal sealed partial class SolverDetailsPanel : PanelContainer
{
    private readonly RichTextLabel _text;
    private readonly ScrollContainer _scroll;
    private SearchUiSnapshot? _lastSearch;
    private (CombatUiPhase, int, int, int, int, string?, string?, string?, string?) _lastSession;
    private bool _hasRendered;

    public SolverDetailsPanel()
    {
        Name = "DetailsPanel";
        MouseFilter = MouseFilterEnum.Pass;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Sm));
        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass,
            CustomMinimumSize = new Vector2(0, 80),
        };
        _text = SolverUiTokens.CreateRichText(SolverUiTokens.Type.Caption);
        _scroll.AddChild(_text);
        AddChild(_scroll);
    }

    /// <summary>Natural text height; the shell clamps the viewport to it.</summary>
    public float NaturalHeight => _text.GetContentHeight();

    public void SetViewportHeight(float height)
    {
        Vector2 size = new(0, height);
        if (_scroll.CustomMinimumSize != size)
            _scroll.CustomMinimumSize = size;
    }

    public void Apply(CombatUiSnapshot snapshot)
    {
        var session = (snapshot.Phase, snapshot.Searches, snapshot.ReusedTurns, snapshot.Deviations,
            snapshot.DeploymentFailures, snapshot.ErrorCode, snapshot.ErrorDetail, snapshot.PauseCode,
            snapshot.PauseReason);
        if (_hasRendered && ReferenceEquals(snapshot.Search, _lastSearch) && session == _lastSession)
            return;
        _hasRendered = true;
        _lastSearch = snapshot.Search;
        _lastSession = session;
        _text.Text = Render(snapshot);
    }

    private static string Render(CombatUiSnapshot snapshot)
    {
        StringBuilder text = new();
        string strong = SolverUiTokens.Palette.Hex(SolverUiTokens.Palette.TextPrimary);
        string muted = SolverUiTokens.Palette.Hex(SolverUiTokens.Palette.TextMuted);
        string warning = SolverUiTokens.Palette.Hex(SolverUiTokens.Palette.Warning);
        string danger = SolverUiTokens.Palette.Hex(SolverUiTokens.Palette.Danger);
        void Heading(string title)
        {
            if (text.Length > 0)
                text.Append('\n');
            text.Append("[b][color=").Append(strong).Append(']').Append(Escape(title)).Append("[/color][/b]\n");
        }
        void Line(string label, string value)
            => text.Append("[color=").Append(muted).Append(']').Append(Escape(label)).Append("[/color]  ")
                .Append(Escape(value)).Append('\n');

        Heading(SolverText.Get("本场全自动"));
        Line(SolverText.Get("搜索次数"), SolverPanelText.Count(snapshot.Searches));
        Line(SolverText.Get("沿用路线的回合"), SolverPanelText.Count(snapshot.ReusedTurns));
        Line(SolverText.Get("预测偏差"), SolverPanelText.Count(snapshot.Deviations));
        Line(SolverText.Get("执行失败"), SolverPanelText.Count(snapshot.DeploymentFailures));
        if (snapshot.ErrorCode is { } code)
        {
            text.Append("[color=").Append(danger).Append(']')
                .Append(Escape(SolverPanelText.Error(code))).Append("[/color]\n");
            if (!string.IsNullOrEmpty(snapshot.ErrorDetail))
                text.Append("[color=").Append(muted).Append(']').Append(Escape(snapshot.ErrorDetail)).Append("[/color]\n");
        }
        if (snapshot.Phase == CombatUiPhase.Paused && !string.IsNullOrEmpty(snapshot.PauseReason))
        {
            text.Append("[color=").Append(warning).Append(']')
                .Append(Escape(SolverPanelText.Pause(snapshot.PauseCode, snapshot.Turn))).Append("[/color]\n");
            Line(SolverText.Get("暂停原文"), snapshot.PauseReason);
        }

        if (snapshot.Search is not { } search)
        {
            Heading(SolverText.Get("搜索"));
            text.Append("[color=").Append(muted).Append(']').Append(Escape(SolverText.Get("本场还没有搜索。")))
                .Append("[/color]\n");
            return text.ToString();
        }

        Heading(search.Phase == SearchUiPhase.Running
            ? SolverText.Format($"第 {search.Turn} 回合的搜索（进行中）")
            : SolverText.Format($"第 {search.Turn} 回合的搜索"));
        Line(SolverText.Get("原因"), SolverPanelText.SearchReason(search.Reason));
        Line(SolverText.Get("档位"), SolverPanelText.Tier(search.Tier) + "  ·  "
            + SolverText.Format($"基准宽度每轮 {SolverPanelText.Seconds(search.BudgetMilliseconds)}"));
        Line(SolverText.Get("搜索线程"), SolverPanelText.Count(search.SearchThreads));
        if (search.Phase == SearchUiPhase.Completed)
        {
            Line(SolverText.Get("结果"), search.Status switch
            {
                "ok" => search.Victory == true ? SolverText.Get("找到胜利路线") : SolverText.Get("没有找到胜利路线"),
                "cancelled" => SolverText.Get("已取消"),
                _ => search.ErrorCode is { } searchCode ? SolverPanelText.Error(searchCode) : SolverText.Get("失败"),
            });
            Line(SolverText.Get("用时"), SolverPanelText.Seconds(search.ElapsedMilliseconds));
            Line(SolverText.Get("展开节点"), SolverPanelText.Count(search.ExpandedNodes));
            Line(SolverText.Get("状态转移"), SolverPanelText.Count(search.Transitions));
            Line(SolverText.Get("路线动作"), SolverPanelText.Count(search.RouteActions));
            Line(SolverText.Get("轮次"), SolverText.Format(
                $"运行 {search.MembersRun} / 计划 {search.Members.Length}，时间不足跳过 {search.MembersSkippedByTime}"));
            if (search.SelectedMember >= 0 && search.SelectedMember < search.Members.Length)
            {
                SearchMemberUi chosen = search.Members[search.SelectedMember];
                Line(SolverText.Get("采用"), SolverText.Format(
                    $"第 {search.SelectedMember + 1} 轮（宽度 {chosen.Width}，{SolverPanelText.Evaluation(chosen.Evaluation)}，{SolverPanelText.PotionLimit(chosen.Potions)}）"));
            }
            Line(SolverText.Get("停止原因"), SolverPanelText.StopReasons(search.StopReason));
            Line(SolverText.Get("搜索内存"), SolverText.Format(
                $"峰值 {SolverPanelText.Bytes(search.SearchMemoryPeakBytes)}，结束时 {SolverPanelText.Bytes(search.SearchMemoryUsedBytes)}"));
            Line(SolverText.Get("工作区峰值"), SolverText.Format(
                $"可增长 {SolverPanelText.Bytes(search.SearchWorkspaceGrowablePeakBytes)}，总计 {SolverPanelText.Bytes(search.SearchWorkspacePeakBytes)}，超限父节点 {SolverPanelText.Count(search.WorkspaceOverflowParents)}"));
            Line(SolverText.Get("进程内存峰值"), SolverText.Format(
                $"托管堆 {SolverPanelText.Bytes(search.ManagedHeapPeakBytes)}，工作集 {SolverPanelText.Bytes(search.WorkingSetPeakBytes)}"));
            if (search.SoftTimeRisk)
                text.Append("[color=").Append(warning).Append(']')
                    .Append(Escape(SolverText.Get("有轮次接近时限，结果可能受机器速度影响。"))).Append("[/color]\n");
            if (search.MemoryBudgetExceeded)
                text.Append("[color=").Append(warning).Append(']')
                    .Append(Escape(SolverText.Get("搜索达到内存上限，后续轮次没有运行。"))).Append("[/color]\n");
        }
        else
        {
            Line(SolverText.Get("已完成轮次"), SolverPanelText.Count(search.MemberRuns.Length));
            Line(SolverText.Get("已展开节点"), SolverPanelText.Count(search.ExpandedNodes));
        }

        if (search.Members.Length > 0)
            AppendMembers(text, search, muted);
        else if (search.MemberRuns.Length > 0)
            AppendRuns(text, search, muted);
        return text.ToString();
    }

    private static void AppendMembers(StringBuilder text, SearchUiSnapshot search, string muted)
    {
        text.Append('\n');
        text.Append("[table=7]");
        foreach (string header in new[]
                 {
                     SolverText.Get("轮"), SolverText.Get("宽度"), SolverText.Get("评价"),
                     SolverText.Get("用药"), SolverText.Get("结果"), SolverText.Get("用时"),
                     SolverText.Get("内存峰值"),
                 })
        {
            text.Append("[cell][color=").Append(muted).Append(']').Append(Escape(header)).Append("[/color][/cell]");
        }
        for (int index = 0; index < search.Members.Length; index++)
        {
            SearchMemberUi member = search.Members[index];
            string evaluation = SolverPanelText.Evaluation(member.Evaluation);
            if (member.Retention != "Standard")
                evaluation += "/" + SolverPanelText.Retention(member.Retention);
            string outcome = !member.Ran
                ? SolverText.Format($"跳过：{SolverPanelText.SkipReason(member.SkippedReason ?? "")}")
                : (member.Victory ? SolverText.Get("胜") : SolverText.Get("未胜"))
                    + (member.HpLost is { } lost ? SolverText.Format($" 失血 {lost}") : "")
                    + " · " + SolverPanelText.StopReason(member.StopReason)
                    + (member.Adopted ? SolverText.Get(" · 采用") : "");
            string number = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (member.Adopted)
                number = "[b]" + number + "[/b]";
            text.Append("[cell]").Append(number).Append("[/cell]")
                .Append("[cell]").Append(member.Width).Append("[/cell]")
                .Append("[cell]").Append(Escape(evaluation)).Append("[/cell]")
                .Append("[cell]").Append(Escape(SolverPanelText.PotionLimit(member.Potions))).Append("[/cell]")
                .Append("[cell]").Append(Escape(outcome)).Append("[/cell]")
                .Append("[cell]").Append(member.Ran ? Escape(SolverPanelText.Seconds(member.ElapsedMilliseconds)) : "-").Append("[/cell]")
                .Append("[cell]").Append(member.Ran ? Escape(SolverPanelText.Bytes(member.MemoryPeakBytes)) : "-").Append("[/cell]");
        }
        text.Append("[/table]\n");
    }

    private static void AppendRuns(StringBuilder text, SearchUiSnapshot search, string muted)
    {
        text.Append('\n');
        text.Append("[table=4]");
        foreach (string header in new[]
                 {
                     SolverText.Get("轮"), SolverText.Get("结果"), SolverText.Get("用时"), SolverText.Get("展开节点"),
                 })
        {
            text.Append("[cell][color=").Append(muted).Append(']').Append(Escape(header)).Append("[/color][/cell]");
        }
        foreach (SearchMemberRunUi run in search.MemberRuns)
        {
            text.Append("[cell]").Append(run.Index + 1).Append("[/cell]")
                .Append("[cell]").Append(Escape(SolverPanelText.StopReason(run.Status))).Append("[/cell]")
                .Append("[cell]").Append(Escape(SolverPanelText.Seconds(run.ElapsedMilliseconds))).Append("[/cell]")
                .Append("[cell]").Append(Escape(SolverPanelText.Count(run.ExpandedNodes))).Append("[/cell]");
        }
        text.Append("[/table]\n");
    }

    /// <summary>Escapes BBCode in names and free text (a "[" in a name must not open a tag).</summary>
    private static string Escape(string value) => value.Replace("[", "[lb]", StringComparison.Ordinal);
}
