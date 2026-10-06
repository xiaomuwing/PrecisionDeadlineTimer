using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PrecisionDeadlineTimer;

/// <summary>
/// Linux 原生定时引擎：timerfd(CLOCK_MONOTONIC) 周期定时 + eventfd 取消 + poll 等待。
/// 与 <see cref="PrecisionDeadlineTimer"/> 相同的 Run 契约：时间表锚定在 Run 开始时刻
/// （第 N 次截止点 = 起始时刻 + N × 周期），由内核 hrtimer 按绝对节拍推进，不累积漂移；
/// 错过的时间点通过 timerfd 过期计数精确上报为 <see cref="TimerTick.SkippedPeriods"/>，不补发。
/// </summary>
/// <remarks>
/// 相对 UniversalUtilities.CrossPlatformTimer 中 LinuxNativeTimer 的三处修复：
/// 1. 定时器在 Run 开始时才武装（timerfd_settime），而非构造时——启动语义与计时原点一致，
///    不存在"构造到 Start 之间的过期次数在首个 tick 被吞掉"的问题；
/// 2. 取消由 CancellationToken 回调写 eventfd 完成，定时循环运行在调用线程上，
///    不存在独立读取线程，也就根除了 Stop 中 Join 无超时、回调内 Stop 自我 Join 死锁的问题；
/// 3. timespec / read / write / poll 的 P/Invoke 全部使用 IntPtr / UIntPtr 尺寸类型，
///    在 time_t / ssize_t / nfds_t 为 32 位的 32 位 Linux 与 64 位 Linux 上布局均正确
///    （仅覆盖 glibc 默认 32 位 time_t；_TIME_BITS=64 的 32 位系统不在范围内）。
/// 精度说明：默认无忙等窗口，唤醒延迟完全由内核调度决定（典型 50~500 µs，高负载下更差）。
/// 设置 SpinWindow 后变为混合等待：timerfd 提前一个窗口唤醒，随后 SpinWait 守到截止点，
/// P99 精度可到 0.05 ms 量级，CPU 代价 ≈ 窗口 ÷ 周期。
/// 设置 RtPriority 后以 SCHED_FIFO 调度定时线程（需 root 或 CAP_SYS_NICE），压制高负载下的尾部尖峰。
/// </remarks>
public sealed class LinuxNativeTimer
{
    /// <summary>
    /// 忙等窗口：每次内核唤醒比截止点提前该时长，醒来后用 <see cref="Thread.SpinWait(int)"/> 守到截止点；
    /// 0（默认）表示纯内核等待。成本约为 SpinWindow ÷ 周期的 CPU 占空比。
    /// </summary>
    public TimeSpan SpinWindow { get; }
    /// <summary>
    /// SCHED_FIFO 实时优先级（1~99）；0（默认）表示不提升。
    /// 无 root / CAP_SYS_NICE 权限时静默退化为普通调度（打印一条警告）。
    /// </summary>
    public int RtPriority { get; }

    private const int ClockMonotonic = 1;
    private const int Cloexec = 0x80000; // O_CLOEXEC / EFD_CLOEXEC / TFD_CLOEXEC 同值
    private const short PollIn = 0x001;
    private const int EIntr = 4;
    private const int SchedFifo = 1;

    /// <summary>
    /// 创建 Linux 原生定时引擎。参数均可省略，默认即纯内核等待、普通调度。
    /// </summary>
    /// <param name="spinWindow">忙等窗口，默认 0（纯内核等待）。大于等于周期时截断为整个周期（退化为全程忙等）。</param>
    /// <param name="rtPriority">SCHED_FIFO 实时优先级（1~99），默认 0（不提升）。</param>
    /// <exception cref="ArgumentOutOfRangeException">spinWindow 为负，或 rtPriority 不在 0~99。</exception>
    public LinuxNativeTimer(TimeSpan? spinWindow = null, int rtPriority = 0)
    {
        SpinWindow = spinWindow ?? TimeSpan.Zero;
        if (SpinWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(spinWindow));
        if (rtPriority is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(rtPriority));
        RtPriority = rtPriority;
    }

    /// <summary>
    /// 在调用线程上运行定时循环，直到 token 取消后返回。契约与
    /// <see cref="PrecisionDeadlineTimer.Run"/> 一致：回调同步执行、必须短小；
    /// 回调耗时计入周期预算，超期的周期计入 SkippedPeriods，不连续补发。
    /// </summary>
    /// <param name="workAction">每个周期执行的回调，接收 <see cref="TimerTick"/>。</param>
    /// <param name="interval">触发周期，必须为正。</param>
    /// <param name="token">取消令牌；取消后 Run 返回。</param>
    /// <exception cref="ArgumentNullException">workAction 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">interval 非正。</exception>
    /// <exception cref="PlatformNotSupportedException">当前不是 Linux。</exception>
    /// <exception cref="Win32Exception">timerfd / eventfd / poll 系统调用失败（errno 见 <see cref="Win32Exception.NativeErrorCode"/>）。</exception>
    /// <remarks>回调中抛出的异常沿 Run 的调用线程传出，由调用者观察。</remarks>
    public unsafe void Run(Action<TimerTick> workAction, TimeSpan interval, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(workAction);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("LinuxNativeTimer 依赖 Linux 的 timerfd/eventfd，仅支持 Linux。");

        // 实时调度只是调度建议，权限不足（无 root / CAP_SYS_NICE）不致命，退化为普通线程继续运行。
        if (RtPriority > 0)
        {
            var param = new SchedParam { sched_priority = RtPriority };
            if (sched_setscheduler(0, SchedFifo, ref param) == -1)
            {
                Console.Error.WriteLine($"警告：设置 SCHED_FIFO 优先级 {RtPriority} 失败（errno={Marshal.GetLastWin32Error()}），" +
                    "需要 root 或 CAP_SYS_NICE（sudo 运行，或在 /etc/security/limits.conf 配置 rtprio）。已退化为普通调度。");
            }
        }

        int timerFd = -1, eventFd = -1;
        GCHandle timerBufferHandle = default, eventBufferHandle = default;
        try
        {
            timerFd = timerfd_create(ClockMonotonic, Cloexec);
            if (timerFd == -1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "timerfd_create 失败。");
            eventFd = eventfd(0, Cloexec);
            if (eventFd == -1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "eventfd 失败。");

            // 两个独立的 pinned 缓冲区：timerfd 读计数 / eventfd 读写取消信号，
            // 避免取消回调与定时循环并发写同一块内存。
            var timerBuffer = new byte[8];
            var eventBuffer = new byte[8];
            timerBufferHandle = GCHandle.Alloc(timerBuffer, GCHandleType.Pinned);
            eventBufferHandle = GCHandle.Alloc(eventBuffer, GCHandleType.Pinned);
            IntPtr timerBufferPtr = timerBufferHandle.AddrOfPinnedObject();
            IntPtr eventBufferPtr = eventBufferHandle.AddrOfPinnedObject();

            // 武装时刻即时间表原点。唤醒点比截止点提前一个忙等窗口（it_value = 周期 − 窗口），
            // 醒来后由 SpinWait 守到真正的截止点；窗口为 0 时退化为纯内核等待。
            // 窗口大于等于周期时唤醒点钳到 100 ns 之后（it_value 为 0 会解除武装），即全程忙等。
            // Linux 上 Stopwatch 与 CLOCK_MONOTONIC 同源，时间戳可直接对齐。
            TimeSpan wakeInterval = SpinWindow >= interval ? TimeSpan.FromTicks(1) : interval - SpinWindow;
            var spec = new ITimerSpec { it_interval = ToTimeSpec(interval), it_value = ToTimeSpec(wakeInterval) };
            long baseStamp = Stopwatch.GetTimestamp();
            if (timerfd_settime(timerFd, 0, ref spec, IntPtr.Zero) == -1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "timerfd_settime 失败。");

            PollFd* fds = stackalloc PollFd[2];
            fds[0] = new PollFd { fd = timerFd, events = PollIn };
            fds[1] = new PollFd { fd = eventFd, events = PollIn };

            long period = Math.Max(1, checked((long)Math.Round(interval.TotalSeconds * Stopwatch.Frequency)));
            long tickCount = 0, skipped = 0;

            // using 先于 finally 处置：保证取消回调不会再触碰 eventFd 后才关闭句柄。
            using (token.Register(() =>
            {
                // 取消只写 eventfd 计数（任何值均可），poll 被唤醒后由循环退出。
                BitConverter.TryWriteBytes(eventBuffer, 1L);
                _ = write(eventFd, eventBufferPtr, (UIntPtr)8);
            }))
            {
                while (true)
                {
                    fds[0].revents = 0;
                    fds[1].revents = 0;
                    int ret = poll((IntPtr)fds, (UIntPtr)2, -1);
                    if (ret < 0)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == EIntr) continue; // 被信号打断，重新等待
                        throw new Win32Exception(error, "poll 失败。");
                    }

                    // 优先检查取消信号。
                    if ((fds[1].revents & PollIn) != 0)
                    {
                        _ = read(eventFd, eventBufferPtr, (UIntPtr)8);
                        break;
                    }

                    if ((fds[0].revents & PollIn) != 0)
                    {
                        if (read(timerFd, timerBufferPtr, (UIntPtr)8).ToInt64() != 8)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error == EIntr) continue;
                            throw new Win32Exception(error, "读取 timerfd 失败。");
                        }

                        // timerfd 读回的是自上次读取以来的过期次数（>=1）：
                        // 回调超预算或系统尖峰导致的错过周期在这里被精确计数，只计数、不补发。
                        long expirations = BitConverter.ToInt64(timerBuffer, 0);
                        tickCount += expirations;
                        skipped += expirations - 1;
                        long scheduled = checked(baseStamp + tickCount * period);
                        // 忙等窗口：内核提前唤醒后，守到真正的截止点再回调；
                        // 已错过截止点（过期补发）时本循环立即退出，不会额外空转。
                        while (Stopwatch.GetTimestamp() < scheduled)
                            Thread.SpinWait(16);
                        long now = Stopwatch.GetTimestamp();
                        long wakeLateness = Math.Max(0, now - scheduled);
                        workAction(new TimerTick(now, scheduled, skipped, wakeLateness, false));
                        skipped = 0;
                        if (token.IsCancellationRequested) break;
                    }
                }
            }
        }
        finally
        {
            if (timerBufferHandle.IsAllocated) timerBufferHandle.Free();
            if (eventBufferHandle.IsAllocated) eventBufferHandle.Free();
            if (eventFd != -1) _ = close(eventFd);
            if (timerFd != -1) _ = close(timerFd);
        }
    }

    /// <summary>
    /// TimeSpan（100 ns 整数刻度）转 timespec，全程整数运算，
    /// 避免原实现 double 乘 1e9 的舍入误差。
    /// </summary>
    private static TimeSpec ToTimeSpec(TimeSpan value)
    {
        long ticks = value.Ticks;
        return new TimeSpec
        {
            tv_sec = (IntPtr)(ticks / 10_000_000),
            tv_nsec = (IntPtr)((ticks % 10_000_000) * 100)
        };
    }

    // time_t 与 long 均为原生字长：IntPtr 使本结构在 32/64 位 Linux 上布局都正确。
    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public IntPtr tv_sec;
        public IntPtr tv_nsec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ITimerSpec
    {
        public TimeSpec it_interval;
        public TimeSpec it_value;
    }

    // struct pollfd：int fd; short events; short revents;
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int timerfd_create(int clockid, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int timerfd_settime(int fd, int flags, ref ITimerSpec newValue, IntPtr oldValue);

    [DllImport("libc", SetLastError = true)]
    private static extern int eventfd(uint initval, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SchedParam { public int sched_priority; }

    // pid 传 0 表示调用线程本身。
    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setscheduler(int pid, int policy, ref SchedParam param);

    // ssize_t / size_t / nfds_t 均为原生字长，用 IntPtr / UIntPtr 兼容 32/64 位。
    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr read(int fd, IntPtr buf, UIntPtr count);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr write(int fd, IntPtr buf, UIntPtr count);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(IntPtr fds, UIntPtr nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
