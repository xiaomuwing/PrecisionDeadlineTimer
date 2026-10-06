using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PrecisionDeadlineTimer;

/// <summary>唤醒迟到或回调超期时的处理方式。</summary>
public enum LatePolicy
{
    /// <summary>保持原固定时间表：迟到后的下一周期缩短以重新对齐，长期零漂移（默认，推荐）。</summary>
    KeepSchedule,
    /// <summary>迟到超过 RebaseAfter 阈值时以当前时刻为原点重排时间表：抑制迟到后的短周期，代价是时间表漂移、平均频率降低。</summary>
    Rebase
}

/// <summary>Windows MMCSS 任务内的相对线程优先级；不改变进程优先级。</summary>
public enum WindowsMmcssPriority
{
    /// <summary>低优先级。</summary>
    Low = -1,
    /// <summary>普通优先级，保持原默认行为。</summary>
    Normal = 0,
    /// <summary>高优先级。</summary>
    High = 1,
    /// <summary>关键任务优先级；适用于短小、无阻塞的定时回调。</summary>
    Critical = 2
}

/// <summary>
/// 每次触发传给回调的数据。所有时间戳单位均为 <see cref="Stopwatch.Frequency"/> 对应的 tick，可除以它换算为秒。
/// </summary>
/// <param name="Timestamp">本次回调的实际触发时刻。</param>
/// <param name="ScheduledTimestamp">本次对应的时间表时刻；发生跳过时为最近一个已到期时间点。</param>
/// <param name="SkippedPeriods">自上次回调以来累计跳过的整周期数（错过的时间点只计数、不补发）。</param>
/// <param name="WakeLatenessTicks">调整时间表之前的唤醒迟到；单独上报，避免跳过时间点后大延迟被隐藏成小相位偏差。</param>
/// <param name="ScheduleReset">本次触发是否执行了 Rebase 重排时间表（仅 <see cref="LatePolicy.Rebase"/> 策略下可能为 true）。</param>
public readonly record struct TimerTick
(
    long Timestamp,
    long ScheduledTimestamp,
    long SkippedPeriods,
    long WakeLatenessTicks = 0,
    bool ScheduleReset = false
);

/// <summary>
/// 固定时间表的软实时定时器。Windows 使用高分辨率等待定时器提前唤醒，
/// 最后 SpinWindow 内忙等；超期时跳过错过的时间点，不连续补发采集。
/// 回调同步执行，必须短小；不要在回调中打印、写盘或做无界阻塞。
/// 可选：UseMmcss 为定时线程注册 MMCSS "Pro Audio" 调度类以抑制被抢占的长尖峰；
/// MinSpinWindow 小于 SpinWindow 时忙等窗口在两者之间自适应（省 CPU，尾部精度略降）。
/// </summary>
public sealed class PrecisionDeadlineTimer
{
    /// <summary>
    /// 忙等窗口上限：每个周期最后这段时间内用 <see cref="Thread.SpinWait(int)"/> 守着截止点。
    /// 实际窗口不超过周期；回调工作很少时理论忙等占空比约为窗口 ÷ 周期（一个逻辑核为 100%）。
    /// 按目标机器的等待返回延迟与 CPU 预算复测选取，窗口与周期没有必须遵守的固定比例。
    /// </summary>
    public TimeSpan SpinWindow { get; }
    /// <summary>迟到处理方式，见 LatePolicy 枚举。</summary>
    public LatePolicy LatePolicy { get; }
    /// <summary>唤醒迟到超过该阈值才触发 Rebase 重排时间表；仅在 <see cref="LatePolicy.Rebase"/> 策略下生效。</summary>
    public TimeSpan RebaseAfter { get; }
    /// <summary>是否在 Run 期间为调用线程注册 MMCSS "Pro Audio" 调度类，并解除 Windows 11 对后台进程的定时器节流（仅 Windows，失败时静默退化；节流解除为进程级设置，Run 退出后不恢复）。</summary>
    public bool UseMmcss { get; }
    /// <summary>自适应忙等窗口的下限；等于 SpinWindow 时窗口固定。缩小窗口可能增加等待晚返回造成的误差，需在目标机器上验证。</summary>
    public TimeSpan MinSpinWindow { get; }

    private WindowsMmcssPriority _mmcssPriority;
    /// <summary>
    /// MMCSS 任务内的相对优先级，默认 Normal。UseMmcss 为 true 且注册成功时生效；
    /// 提高优先级可减少普通线程抢占，不能避免系统中断或保证最坏延迟。设置失败时保持已注册的调度等级。
    /// </summary>
    public WindowsMmcssPriority MmcssPriority
    {
        get => _mmcssPriority;
        init
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(MmcssPriority));
            _mmcssPriority = value;
        }
    }

    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;
    private const int ProcessPowerThrottlingClass = 4;
    /// <summary>
    /// 创建固定时间表定时器。所有参数均可省略，具体精度需在目标机器和回调负载下验证。
    /// </summary>
    /// <param name="spinWindow">忙等窗口上限，默认 1.5 ms，实际不超过周期。
    /// 理论忙等占空比约为窗口 ÷ 周期；应依据实测等待返回延迟与 CPU 预算调整，不能由窗口大小保证最坏精度。</param>
    /// <param name="latePolicy">迟到处理方式，默认 <see cref="LatePolicy.KeepSchedule"/>。</param>
    /// <param name="rebaseAfter">Rebase 策略的重排阈值，默认 0.1 ms；KeepSchedule 策略下无效。</param>
    /// <param name="useMmcss">是否为定时线程注册 MMCSS "Pro Audio" 并解除后台进程定时器节流，默认 true。</param>
    /// <param name="minSpinWindow">自适应忙等窗口下限；省略或与 spinWindow 相等时窗口固定。</param>
    /// <exception cref="ArgumentOutOfRangeException">参数为负、枚举未定义，或 minSpinWindow 大于 spinWindow。</exception>
    public PrecisionDeadlineTimer(TimeSpan? spinWindow = null, LatePolicy latePolicy = LatePolicy.KeepSchedule, TimeSpan? rebaseAfter = null, bool useMmcss = true, TimeSpan? minSpinWindow = null)
    {
        SpinWindow = spinWindow ?? TimeSpan.FromMilliseconds(1.5);
        if (SpinWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(spinWindow));
        }
        if (!Enum.IsDefined(latePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(latePolicy));
        }
        LatePolicy = latePolicy;
        RebaseAfter = rebaseAfter ?? TimeSpan.FromMilliseconds(0.1);
        if (RebaseAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(rebaseAfter));
        }
        UseMmcss = useMmcss;
        MinSpinWindow = minSpinWindow ?? SpinWindow;
        if (MinSpinWindow < TimeSpan.Zero || MinSpinWindow > SpinWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(minSpinWindow));
        }

    }

    /// <summary>
    /// 在调用线程上运行定时循环，直到 token 取消后返回。
    /// 时间表锚定在首次调用时刻：第 N 次截止点 = 起始时刻 + N × interval，抖动不会累积。
    /// 回调同步执行、必须短小；回调耗时计入周期预算，超期将跳过整周期并计数，不连续补发。
    /// 界面、写盘、日志等慢操作请交给其他线程。
    /// </summary>
    /// <param name="workAction">每个周期执行的回调，接收 <see cref="TimerTick"/>。</param>
    /// <param name="interval">触发周期，必须为正。</param>
    /// <param name="token">取消令牌；取消后 Run 返回。</param>
    /// <exception cref="ArgumentNullException">workAction 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">interval 非正。</exception>
    /// <exception cref="Win32Exception">Windows 高分辨率等待定时器创建或装填失败（仅在 Win10 1803+ 使用系统定时器时可能抛出）。</exception>
    /// <remarks>回调中抛出的异常会沿 Run 的调用线程传出，由调用者观察。</remarks>
    public void Run(Action<TimerTick> workAction, TimeSpan interval, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(workAction);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        long period = Math.Max(1, checked((long)Math.Round(interval.TotalSeconds * Stopwatch.Frequency)));
        long maxSpinTicks = Math.Min(period, checked((long)Math.Round(SpinWindow.TotalSeconds * Stopwatch.Frequency)));
        long minSpinTicks = Math.Min(maxSpinTicks, checked((long)Math.Round(MinSpinWindow.TotalSeconds * Stopwatch.Frequency)));
        long rebaseTicks = checked((long)Math.Round(RebaseAfter.TotalSeconds * Stopwatch.Frequency));
        bool adaptive = minSpinTicks < maxSpinTicks;
        // 险些迟到的判据（0.05 ms）；大迟到是线程被抢占，加宽窗口救不回来，不响应。
        long nearMissTicks = Math.Max(1, Stopwatch.Frequency / 20_000);
        using var timer = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134) ? new WindowsTimer() : null;
        WaitHandle[]? handles = timer is null ? null : [token.WaitHandle, timer];

        // MMCSS 只是调度建议，失败（如服务受限环境）不致命，退化为普通线程继续运行。
        // 同时解除 Windows 11 对后台进程的电源节流：否则定时器到期会被合并延迟数毫秒，
        // 长周期（数百 ms 级）睡眠后的唤醒尖峰多源于此。设置为进程级，Run 退出后不恢复。
        IntPtr mmcss = IntPtr.Zero;
        if (UseMmcss && OperatingSystem.IsWindows())
        {
            uint taskIndex = 0;
            mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
            if (mmcss != IntPtr.Zero && MmcssPriority != WindowsMmcssPriority.Normal)
                _ = AvSetMmThreadPriority(mmcss, (int)MmcssPriority);
            var throttling = new PowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = ProcessPowerThrottlingIgnoreTimerResolution,
                StateMask = 0,
            };
            _ = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingClass, ref throttling, Marshal.SizeOf(throttling));
        }
        long spinTicks = maxSpinTicks;
        long deadline = checked(Stopwatch.GetTimestamp() + period);
        long skipped = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                long wakeRemaining = WaitUntil(deadline, spinTicks, timer, handles, token);
                if (token.IsCancellationRequested) break;
                if (adaptive && wakeRemaining >= 0)
                {
                    // 唤醒时窗口富余过半就缓慢收缩；险些迟到才放宽；下限 MinSpinWindow、上限 SpinWindow。
                    if (wakeRemaining > spinTicks / 2)
                        spinTicks = Math.Max(minSpinTicks, (long)(spinTicks * 0.99));
                    else if (wakeRemaining < nearMissTicks)
                        spinTicks = Math.Min(maxSpinTicks, (long)(spinTicks * 1.05) + 1);
                }

                long now = Stopwatch.GetTimestamp();
                long wakeLateness = Math.Max(0, now - deadline);
                bool rebase = LatePolicy == LatePolicy.Rebase && wakeLateness > rebaseTicks;
                // 若唤醒已晚了整周期，将本次对应到最近的已到期时间点。
                // 不会为过去的采样时间连续触发多个回调。
                long missedOnWake = Math.Max(0, (now - deadline) / period);
                deadline = checked(deadline + missedOnWake * period);
                skipped += missedOnWake;
                // 单独报告调整时间表前的迟到，避免跳过时间点后把大延迟隐藏在小相位偏差里。
                workAction(new TimerTick(now, deadline, skipped, wakeLateness, rebase));
                skipped = 0;
                // Rebase 抑制长延迟后的短周期，代价是时间表漂移和平均频率降低。
                deadline = checked((rebase ? now : deadline) + period);
                long afterWork = Stopwatch.GetTimestamp();
                if (afterWork >= deadline)
                {
                    skipped = (afterWork - deadline) / period + 1;
                    deadline = checked(deadline + skipped * period);
                }
            }
        }
        finally
        {
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
        }
    }
    /// <summary>
    /// 等待到 deadline。返回值：粗等待结束、首次进入忙等那一刻的剩余时间（tick），
    /// 用于自适应窗口；-1 表示本周期未经粗等待或被取消，无有效样本。
    /// </summary>
    private static long WaitUntil(long deadline, long spinTicks, WindowsTimer? timer, WaitHandle[]? handles, CancellationToken token)
    {
        bool armed = false;
        long wakeRemaining = -1;
        while (!token.IsCancellationRequested)
        {
            long remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0) return wakeRemaining;
            if (remaining > spinTicks && timer is not null)
            {
                // Win32 相对到期时间以负数、100 ns 为单位；QPC 时间表不受墙钟调整影响。
                long dueTime = -Math.Max(1, (long)((remaining - spinTicks) * (10_000_000.0 / Stopwatch.Frequency)));
                timer.Arm(dueTime);
                armed = true;
                if (WaitHandle.WaitAny(handles!) == 0) return -1;
            }
            else if (timer is null && remaining - spinTicks > Stopwatch.Frequency / 500)
            {
                // 其他平台使用保守的粗等待，提前至少约 1 ms 返回后再检查时间。
                int milliseconds = (int)Math.Min(int.MaxValue, (remaining - spinTicks) * (1000.0 / Stopwatch.Frequency) - 1);
                if (token.WaitHandle.WaitOne(milliseconds)) return -1;
            }
            else
            {
                if (armed && wakeRemaining < 0) wakeRemaining = remaining;
                // Thread.SpinWait 不主动让出执行权；SpinWait.SpinOnce 会逐渐 Yield。
                Thread.SpinWait(16);
            }
        }
        return -1;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version; public uint ControlMask; public uint StateMask; }
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState state, int size);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvSetMmThreadPriority(IntPtr mmcssHandle, int priority);
    [DllImport("avrt.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr mmcssHandle);
    private sealed class WindowsTimer : WaitHandle
    {
        public WindowsTimer()
        {
            SafeWaitHandle handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x00100002);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "无法创建 Windows 高分辨率等待定时器。");
            }
            SafeWaitHandle = handle;
        }

        public void Arm(long dueTime)
        {
            if (!SetWaitableTimerEx(SafeWaitHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint desiredAccess);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);
    }
}
