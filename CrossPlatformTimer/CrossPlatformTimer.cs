namespace PrecisionDeadlineTimer;

/// <summary>
/// 跨平台定时器封装：按操作系统自动路由到两个原生引擎——
/// Linux 走 <see cref="LinuxNativeTimer"/>（timerfd + eventfd + poll），
/// 其余平台走 <see cref="PrecisionDeadlineTimer"/>（高分辨率等待定时器 + 忙等）。
/// 对外只暴露统一的 <see cref="Run"/> 契约与 <see cref="TimerTick"/> 语义，
/// 调用方写一份代码即可在两个平台运行。
/// </summary>
/// <remarks>
/// 参数分两类：平台共用的（spinWindow）与平台专属的——
/// latePolicy / rebaseAfter / useMmcss / minSpinWindow / MmcssPriority 仅 Windows 引擎有效，
/// rtPriority 仅 Linux 引擎有效；专属参数在另一平台上被静默忽略（不抛异常）。
/// 注意两个引擎的语义差异：Windows 引擎的忙等窗口默认开启（1.5 ms），
/// Linux 引擎默认纯内核等待（窗口 0）；跨平台部署时建议显式指定 spinWindow 以获得一致行为。
/// </remarks>
public sealed class CrossPlatformTimer
{
    /// <summary>
    /// 忙等窗口；null（默认）表示使用各引擎自身默认（Windows 1.5 ms，Linux 0 = 纯内核等待）。
    /// 成本约为 SpinWindow ÷ 周期的 CPU 占空比。
    /// </summary>
    public TimeSpan? SpinWindow { get; }
    /// <summary>迟到处理方式（仅 Windows 引擎；Linux 引擎由内核按绝对节拍推进，天然等价于 KeepSchedule）。</summary>
    public LatePolicy LatePolicy { get; }
    /// <summary>Rebase 策略的重排阈值（仅 Windows 引擎）。</summary>
    public TimeSpan RebaseAfter { get; }
    /// <summary>是否注册 MMCSS "Pro Audio" 调度类并解除 Win11 后台节流（仅 Windows 引擎）。</summary>
    public bool UseMmcss { get; }
    /// <summary>MMCSS 任务内相对优先级（仅 Windows），默认 Normal；仅 UseMmcss 开启时生效。</summary>
    public WindowsMmcssPriority MmcssPriority { get; init; } = WindowsMmcssPriority.Normal;
    /// <summary>自适应忙等窗口下限（仅 Windows 引擎）；null 表示与 SpinWindow 相等（固定窗口）。</summary>
    public TimeSpan? MinSpinWindow { get; }
    /// <summary>SCHED_FIFO 实时优先级 1~99（仅 Linux 引擎）；0（默认）不提升，无权限时静默退化。</summary>
    public int RtPriority { get; }
    /// <summary>当前平台实际使用的引擎名："linux" 或 "spin"。</summary>
    public static string EngineName => OperatingSystem.IsLinux() ? "linux" : "spin";

    /// <summary>
    /// 创建跨平台定时器。所有参数均可省略；平台专属参数在另一平台上静默忽略。
    /// </summary>
    /// <param name="spinWindow">忙等窗口；省略时用引擎默认（Windows 1.5 ms，Linux 0）。</param>
    /// <param name="latePolicy">迟到处理方式（仅 Windows），默认 <see cref="LatePolicy.KeepSchedule"/>。</param>
    /// <param name="rebaseAfter">Rebase 策略的重排阈值（仅 Windows），默认 0.1 ms。</param>
    /// <param name="useMmcss">是否注册 MMCSS（仅 Windows），默认 true。</param>
    /// <param name="minSpinWindow">自适应忙等窗口下限（仅 Windows），省略时窗口固定。</param>
    /// <param name="rtPriority">SCHED_FIFO 实时优先级（仅 Linux），默认 0（不提升）。</param>
    /// <exception cref="ArgumentOutOfRangeException">spinWindow 为负、枚举未定义，或 rtPriority 不在 0~99。</exception>
    public CrossPlatformTimer(TimeSpan? spinWindow = null, LatePolicy latePolicy = LatePolicy.KeepSchedule,
        TimeSpan? rebaseAfter = null, bool useMmcss = true, TimeSpan? minSpinWindow = null, int rtPriority = 0)
    {
        if (spinWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(spinWindow));
        if (!Enum.IsDefined(latePolicy))
            throw new ArgumentOutOfRangeException(nameof(latePolicy));
        if (rtPriority is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(rtPriority));
        SpinWindow = spinWindow;
        LatePolicy = latePolicy;
        RebaseAfter = rebaseAfter ?? TimeSpan.FromMilliseconds(0.1);
        UseMmcss = useMmcss;
        MinSpinWindow = minSpinWindow;
        RtPriority = rtPriority;
    }

    /// <summary>
    /// 在调用线程上运行定时循环，直到 token 取消后返回。语义与两个引擎一致：
    /// 时间表锚定在 Run 开始时刻（第 N 次截止点 = 起始时刻 + N × interval），抖动不累积；
    /// 回调同步执行、必须短小；超期的时间点只计数（<see cref="TimerTick.SkippedPeriods"/>）、不补发。
    /// </summary>
    /// <param name="workAction">每个周期执行的回调，接收 <see cref="TimerTick"/>。</param>
    /// <param name="interval">触发周期，必须为正。</param>
    /// <param name="token">取消令牌；取消后 Run 返回。</param>
    /// <exception cref="ArgumentNullException">workAction 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">interval 非正。</exception>
    /// <remarks>回调中抛出的异常沿 Run 的调用线程传出，由调用者观察。</remarks>
    public void Run(Action<TimerTick> workAction, TimeSpan interval, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(workAction);
        if (OperatingSystem.IsLinux())
        {
            new LinuxNativeTimer(SpinWindow, RtPriority).Run(workAction, interval, token);
        }
        else
        {
            new PrecisionDeadlineTimer(SpinWindow, LatePolicy, RebaseAfter, UseMmcss, MinSpinWindow)
                { MmcssPriority = MmcssPriority }
                .Run(workAction, interval, token);
        }
    }
}
