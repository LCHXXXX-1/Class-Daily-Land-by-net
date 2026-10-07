using System.Diagnostics;
using System.Windows.Threading;
using ClassDailyLand.App.Views.Controls;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 灵动岛「情景演示」。
/// 对应源模块：dynamic_island.py 的 start_scenario_test / _scenario_* 系列，
/// 以及 controller.py 的 run_scenario_test。
///
/// 用途：让**真实灵动岛**按压缩后的时间轴跑完整一轮情景 ——
/// 提前提醒 → 倒计时（蓝条走完） → 「上课了！」 → 「下课了！」 → 恢复常规显示。
/// 由状态测试对话框的「▶ 开始演示」按钮触发。
///
/// 时间轴（speed 为倍速，全部时长按 speed 等比压缩）：
/// <code>
///   0            T1              T2                 T3            T4
///   ├── advance ──┼── countdown ──┼────── up ────────┼─── down ────┤ 结束
///   距离上课 测试    测试倒计时       上课了！            下课了！
/// </code>
/// </summary>
public partial class IslandWindow
{
    /// <summary>情景测试里单条提醒的展示时长（对应 SCENARIO_ALERT_MS）。</summary>
    private const int ScenarioAlertMs = 1000;

    /// <summary>演示驱动间隔。60ms 让文字秒数与蓝条都足够顺滑。</summary>
    private const int ScenarioTickMs = 60;

    private DispatcherTimer? _scenarioTimer;
    private readonly Stopwatch _scenarioClock = new();

    /// <summary>演示代号。每次启动自增，用于作废旧定时器的迟到回调。</summary>
    private int _scenarioId;

    private bool _testing;
    private string _testPhase = "";

    private double _scenarioAdvanceTotal;      // 缩放后的提前提醒时长（秒）
    private double _scenarioCountdownTotal;    // 缩放后的倒计时窗口（秒）
    private double _scenarioT1, _scenarioT2, _scenarioT3, _scenarioT4;

    /// <summary>演示开始前岛是关闭状态 → 演示结束后要恢复成关闭。</summary>
    private bool _scenarioRestoreDisabled;

    /// <summary>是否正在演示。演示期间状态机主循环让位给情景时间轴。</summary>
    public bool IsTesting => _testing;

    /// <summary>
    /// 启动整段情景演示（对应 start_scenario_test）。
    /// 返回 false 表示当前无法演示（岛被禁用且唤醒失败）。
    /// </summary>
    /// <param name="advanceSec">提前提醒时长（秒，原始值，会按倍速压缩）。</param>
    /// <param name="countdownSec">倒计时窗口（秒，原始值）。</param>
    /// <param name="endHoldSec">「上课了！」到「下课了！」之间的停留（秒，原始值）。</param>
    /// <param name="speed">演示倍速，越大越快。</param>
    public bool StartScenarioTest(int advanceSec, int countdownSec, int endHoldSec, double speed = 10.0)
    {
        // 倍速异常时回落到 10×（与源项目一致）
        if (!(speed >= 0.1)) speed = 10.0;

        advanceSec = Math.Max(0, advanceSec);
        countdownSec = Math.Max(1, countdownSec);
        endHoldSec = Math.Max(0, endHoldSec);

        StopScenarioTimer();
        var sid = ++_scenarioId;

        // 演示必须看得见：原本关闭则临时打开，结束后再关回去
        _scenarioRestoreDisabled = !_enabled;
        if (_scenarioRestoreDisabled) SetEnabled(true);

        PushUpPanels(immediate: true);

        _testing = true;
        _alertText = null;
        _countdownDismissedKey = "";      // 演示不受「本轮已收起倒计时」的记忆影响

        _scenarioAdvanceTotal = advanceSec / speed;
        _scenarioCountdownTotal = Math.Max(1.0, countdownSec / speed);

        // 各阶段结束时刻（秒）
        _scenarioT1 = _scenarioAdvanceTotal;
        _scenarioT2 = _scenarioT1 + _scenarioCountdownTotal;
        _scenarioT3 = _scenarioT2 + (ScenarioAlertMs + endHoldSec * 1000.0 / speed) / 1000.0;
        _scenarioT4 = _scenarioT3 + ScenarioAlertMs / 1000.0;

        _testPhase = "";
        _scenarioClock.Restart();

        _scenarioTimer = NewTimer(
            TimeSpan.FromMilliseconds(ScenarioTickMs),
            DispatcherPriority.Render,
            () =>
            {
                // 已启动新演示 / 已收尾 → 旧回调直接作废
                if (sid == _scenarioId && _testing) ScenarioTick();
            });

        _scenarioTimer.Start();

        // 立即渲染第一帧，避免最长 60ms 的空白
        ScenarioTick();
        return true;
    }

    /// <summary>中止演示并恢复常规显示（供退出 / 关闭岛时调用）。</summary>
    public void StopScenarioTest()
    {
        if (!_testing) return;

        _scenarioId++;
        EndScenarioTest();
    }

    // ================= 时间轴 =================

    private void StopScenarioTimer()
    {
        _scenarioTimer?.Stop();
        _scenarioTimer = null;
    }

    private string ResolvePhase(double elapsed)
    {
        if (elapsed < _scenarioT1) return "advance";
        if (elapsed < _scenarioT2) return "countdown";
        if (elapsed < _scenarioT3) return "up";
        if (elapsed < _scenarioT4) return "down";
        return "end";
    }

    private void ScenarioTick()
    {
        var elapsed = _scenarioClock.Elapsed.TotalSeconds;
        var phase = ResolvePhase(elapsed);

        if (phase != _testPhase)
        {
            _testPhase = phase;
            EnterScenarioPhase(phase);

            if (!_testing) return;   // end 阶段已收尾
        }

        switch (phase)
        {
            case "advance":
                RenderScenarioAdvance(elapsed);
                break;

            case "countdown":
                RenderScenarioCountdown(elapsed);
                break;
        }
    }

    private void EnterScenarioPhase(string phase)
    {
        switch (phase)
        {
            case "advance":
                _state = IslandState.Compact;
                AnimateVisualWidth(CompactWidth + Visual.ExtraWidth);
                break;

            case "countdown":
                _state = IslandState.Countdown;
                AnimateVisualWidth(CountdownWidth);
                break;

            case "up":
                ShowScenarioAlert("上课了！", isEnd: false);
                break;

            case "down":
                ShowScenarioAlert("下课了！", isEnd: true);
                break;

            case "end":
                EndScenarioTest();
                break;
        }
    }

    /// <summary>阶段一：提前提醒（对应 _scenario_advance 的文案）。</summary>
    private void RenderScenarioAdvance(double elapsed)
    {
        var total = Math.Max(0.001, _scenarioAdvanceTotal);
        var left = Math.Max(0.0, total - elapsed);
        var remain = (int)Math.Round(left);

        Visual.SetCompactState(
            $"距离上课 测试 · {remain}s",
            IslandVisual.Accent(IslandAccent.Blue),
            Math.Clamp(left / total, 0, 1),
            -1,
            Visual.ExtraWidth);

        ResizeToContent();
        PublishGeometry();
    }

    /// <summary>阶段二：倒计时（蓝条从 0 涨到满，与源项目 _fill_progress 方向一致）。</summary>
    private void RenderScenarioCountdown(double elapsed)
    {
        var total = Math.Max(0.001, _scenarioCountdownTotal);
        var left = Math.Max(0.0, _scenarioT2 - elapsed);
        var remain = (int)Math.Round(left);

        // 蓝条 = 剩余比例（对应源项目 ratio = remain / window）：
        // 开局接近满格，右缘随时间从右往左缩 —— 不是「已流逝比例」从左往右长
        var remainingRatio = Math.Clamp(left / total, 0, 1);

        Visual.SetCompactState(
            $"测试倒计时 · {remain}s",
            IslandVisual.Accent(IslandAccent.Blue),
            remainingRatio,
            remainingRatio,
            Visual.ExtraWidth);

        ResizeToContent();
        PublishGeometry();
    }

    /// <summary>
    /// 情景内的提醒。与常规 <see cref="ShowAlert"/> 的区别：
    /// 结束时机完全由情景时间轴决定，不受 island_alert_hold_ms 影响。
    /// </summary>
    private void ShowScenarioAlert(string text, bool isEnd)
    {
        _alertText = text;
        _alertIsEnd = isEnd;
        _alertUntil = DateTime.MaxValue;      // 由时间轴负责清空

        RenderAlert();

        var width = Math.Max(120, _current.IslandAlertWidth);
        AnimateVisualWidth(width);
    }

    /// <summary>收尾：回到常规显示；若演示前岛是关的，则恢复成关闭。</summary>
    private void EndScenarioTest()
    {
        StopScenarioTimer();

        _testing = false;
        _testPhase = "";
        _alertText = null;
        _alertUntil = DateTime.MinValue;
        _countdownDismissedKey = "";
        _lastStatus = null;      // 让下一次常规刷新重新判定状态，不误报「上课了」

        if (_scenarioRestoreDisabled)
        {
            _scenarioRestoreDisabled = false;
            SetEnabled(false);
            return;
        }

        _state = IslandState.Compact;

        var target = CompactWidth + Visual.ExtraWidth;
        AnimateVisualWidth(target);

        RefreshImpl();
    }
}
