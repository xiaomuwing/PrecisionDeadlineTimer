using System.Diagnostics;
using System.Globalization;

namespace PrecisionDeadlineTimer;

/// <summary>PrecisionDeadlineTimer 基准：预分配采样、预热排除、双侧误差与分时稳定性统计。</summary>
internal static class TimerBenchmark
{
    private readonly record struct Sample(long Timestamp, long ScheduledTimestamp, long Skipped,
        long WakeLateness, bool ScheduleReset);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Contains("--help"))
            {
                Console.WriteLine("参数: --seconds 200 --interval-ms 200 " +
                    "--spin-ms 15 --min-spin-ms 15 --mmcss true --work-ms 0 --priority normal|above " +
                    "--late-policy keep|rebase --engine auto|spin|linux --rt-priority 0 " +
                    "--rebase-after-ms 0.1 --warmup-seconds 20 --tolerance-ms 0.5 --output samples.csv");
                return 0;
            }
            var options = new Dictionary<string, string>();
            var allowed = new HashSet<string> { "--seconds", "--interval-ms", "--spin-ms",
                "--work-ms", "--priority", "--output", "--late-policy", "--rebase-after-ms", "--warmup-seconds", "--tolerance-ms",
                "--min-spin-ms", "--mmcss", "--engine", "--rt-priority" };
            for (int index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !allowed.Contains(args[index]))
                    throw new ArgumentException($"未知参数或缺少参数值: {args[index]}");
                options.Add(args[index], args[index + 1]);
            }

            double seconds = Number(options, "--seconds", 100, 0.05, 86400);
            double warmupSeconds = Number(options, "--warmup-seconds", 20, 0, 3600);
            double toleranceMs = Number(options, "--tolerance-ms", 0.5, 0, 60000);
            double intervalMs = Number(options, "--interval-ms", 200, 0.1, 60000);
            double spinMs = Number(options, "--spin-ms", 15, 0, 60000);
            double minSpinMs = Number(options, "--min-spin-ms", spinMs, 0, 60000);
            if (minSpinMs > spinMs)
                throw new ArgumentException("--min-spin-ms 不能大于 --spin-ms。");
            string mmcssName = options.GetValueOrDefault("--mmcss", "true");
            bool mmcss = mmcssName switch
            {
                "true" => true,
                "false" => false,
                _ => throw new ArgumentException("--mmcss 必须是 true 或 false。")
            };
            double workMs = Number(options, "--work-ms", 0, 0, 60000);
            string latePolicyName = options.GetValueOrDefault("--late-policy", "keep");
            LatePolicy latePolicy = latePolicyName switch
            {
                "keep" => LatePolicy.KeepSchedule,
                "rebase" => LatePolicy.Rebase,
                _ => throw new ArgumentException("--late-policy 必须是 keep 或 rebase。")
            };
            double rebaseAfterMs = Number(options, "--rebase-after-ms", 0.1, 0, 60000);
            string priorityName = options.GetValueOrDefault("--priority", "above");
            ThreadPriority priority = priorityName switch
            {
                "normal" => ThreadPriority.Normal,
                "above" => ThreadPriority.AboveNormal,
                _ => throw new ArgumentException("--priority 必须是 normal 或 above。")
            };
            string engineName = options.GetValueOrDefault("--engine", "auto");
            bool useLinux = engineName switch
            {
                // auto：Linux 上默认走 timerfd 引擎，其余平台走忙等引擎。
                "auto" => OperatingSystem.IsLinux(),
                "spin" => false,
                "linux" => true,
                _ => throw new ArgumentException("--engine 必须是 auto、spin 或 linux。")
            };
            int rtPriority = (int)Number(options, "--rt-priority", 0, 0, 99);

            Console.WriteLine($"目标 {intervalMs:F3} ms；预热 {warmupSeconds:F2} 秒、测量 {seconds:F2} 秒；" +
                $"模拟工作 {workMs:F3} ms；优先级 {priorityName}。");
            if (useLinux)
            {
                Console.WriteLine(spinMs > 0
                    ? $"定时引擎: linux（timerfd + eventfd + poll，内核提前唤醒 + 忙等 {Math.Min(spinMs, intervalMs):F3} ms/周期）。"
                    : "定时引擎: linux（timerfd + eventfd + poll，纯内核等待，无忙等窗口）。");
                Console.WriteLine(rtPriority > 0
                    ? $"实时调度: SCHED_FIFO 优先级 {rtPriority}（需 root 或 CAP_SYS_NICE，失败自动退化）。"
                    : "实时调度: 关（--rt-priority 1~99 可启用 SCHED_FIFO）。");
                if (!mmcss || minSpinMs != spinMs)
                    Console.WriteLine("注意：--min-spin-ms / --mmcss 仅对 spin 引擎有效，linux 引擎下被忽略。");
            }
            else
            {
                Console.WriteLine(minSpinMs < spinMs
                    ? $"定时引擎: spin（忙等窗口在 {Math.Min(minSpinMs, intervalMs):F3}～{Math.Min(spinMs, intervalMs):F3} ms/周期间自适应）。"
                    : $"定时引擎: spin（最多忙等 {Math.Min(spinMs, intervalMs):F3} ms/周期）。");
                if (rtPriority > 0)
                    Console.WriteLine("注意：--rt-priority 仅对 linux 引擎有效，spin 引擎下被忽略。");
            }
            Console.WriteLine("定时线程不逐次打印。");
            Console.WriteLine($"迟到策略: {latePolicyName}，重排阈值 {rebaseAfterMs:F3} ms；" +
                $"MMCSS: {(mmcss ? "开" : "关")}。");

            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                Console.WriteLine($"\n正在运行，约需 {seconds + warmupSeconds:F1} 秒，Ctrl+C 可停止。");
                await MeasureAsync(seconds, warmupSeconds, toleranceMs, intervalMs, spinMs,
                    minSpinMs, mmcss, workMs,
                    priority, latePolicy, rebaseAfterMs, useLinux, rtPriority, engineName, options.GetValueOrDefault("--output"), cancellation.Token);
            }
            finally { Console.CancelKeyPress -= cancelHandler; }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static double Number(Dictionary<string, string> options, string key, double fallback,
        double minimum, double maximum)
    {
        double value = options.TryGetValue(key, out string? text)
            ? double.Parse(text, CultureInfo.InvariantCulture) : fallback;
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(key, $"{key} 需在 {minimum} 到 {maximum} 之间。");
        return value;
    }

    private static async Task MeasureAsync(double seconds, double warmupSeconds, double toleranceMs, double intervalMs,
        double spinMs, double minSpinMs, bool mmcss, double workMs, ThreadPriority priority, LatePolicy latePolicy,
        double rebaseAfterMs, bool useLinux, int rtPriority, string engineName, string? output, CancellationToken token)
    {
        // 按四倍理论采样数预分配，避免测量线程上的 List 扩容和逐周期分配。
        double requestedCapacity = Math.Ceiling(seconds * 1000 / intervalMs) * 4 + 32;
        if (requestedCapacity > 20_000_000)
            throw new ArgumentException("样本数过多，请缩短测量时间或增大周期。");
        var samples = new Sample[(int)requestedCapacity];
        int count = 0;
        long frequency = Stopwatch.Frequency;
        long period = (long)Math.Round(intervalMs * frequency / 1000);
        long workTicks = (long)Math.Round(workMs * frequency / 1000);
        long measurementStart = 0, measurementEnd = 0, skippedTotal = 0;
        using var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var process = Process.GetCurrentProcess();
        TimeSpan cpuStart = process.TotalProcessorTime;
        long wallStart = Stopwatch.GetTimestamp();
        int[] gcStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

        void Record(TimerTick tick)
        {
            long now = Stopwatch.GetTimestamp();
            if (measurementStart == 0)
            {
                Thread.CurrentThread.Priority = priority;
                measurementStart = checked(now + (long)Math.Round(warmupSeconds * frequency));
                measurementEnd = checked(measurementStart + (long)Math.Round(seconds * frequency));
            }
            if (now >= measurementEnd)
            {
                localCancellation.Cancel();
                return;
            }
            if (now >= measurementStart)
            {
                if (count == samples.Length)
                    throw new InvalidOperationException("样本缓冲区已满，定时器可能存在异常补发。");
                samples[count++] = new Sample(now, tick.ScheduledTimestamp, tick.SkippedPeriods,
                    tick.WakeLatenessTicks, tick.ScheduleReset);
                skippedTotal += tick.SkippedPeriods;
            }

            // 替换这里为真实采集；界面、写盘、日志交给其他线程，并监控队列容量。
            long workEnd = checked(Stopwatch.GetTimestamp() + workTicks);
            while (Stopwatch.GetTimestamp() < workEnd) Thread.SpinWait(16);
        }

        // 三个选项共用同一套采样与统计管线：Run 契约一致（同步回调 TimerTick，取消后返回）。
        // auto 走跨平台封装层 CrossPlatformTimer（内部按 OS 路由）；显式指定则直连对应引擎。
        Action<Action<TimerTick>, TimeSpan, CancellationToken> runLoop = engineName switch
        {
            "auto" => new CrossPlatformTimer(TimeSpan.FromMilliseconds(spinMs), latePolicy,
                TimeSpan.FromMilliseconds(rebaseAfterMs), mmcss, TimeSpan.FromMilliseconds(minSpinMs), rtPriority).Run,
            "linux" => new LinuxNativeTimer(TimeSpan.FromMilliseconds(spinMs), rtPriority).Run,
            _ => new PrecisionDeadlineTimer(TimeSpan.FromMilliseconds(spinMs), latePolicy,
                TimeSpan.FromMilliseconds(rebaseAfterMs), mmcss, TimeSpan.FromMilliseconds(minSpinMs)).Run,
        };
        await Task.Factory.StartNew(() => runLoop(Record, TimeSpan.FromMilliseconds(intervalMs),
            localCancellation.Token), CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        double elapsedSeconds = (Stopwatch.GetTimestamp() - wallStart) / (double)frequency;
        double cpuCorePercent = (process.TotalProcessorTime - cpuStart).TotalSeconds / elapsedSeconds * 100;
        int[] gcCollections = [GC.CollectionCount(0) - gcStart[0], GC.CollectionCount(1) - gcStart[1],
            GC.CollectionCount(2) - gcStart[2]];
        Console.WriteLine("\n--- 统计结果 ---");
        if (count < 2) { Console.WriteLine("样本不足。"); return; }

        double toMs = 1000.0 / frequency;
        var intervals = new double[count - 1];
        var errors = new double[count - 1];
        var phase = new double[count];
        var wakeLateness = new double[count];
        int scheduleResets = 0;
        for (int index = 0; index < count; index++)
        {
            phase[index] = (samples[index].Timestamp - samples[index].ScheduledTimestamp) * toMs;
            wakeLateness[index] = samples[index].WakeLateness * toMs;
            if (samples[index].ScheduleReset) scheduleResets++;
            if (index == 0) continue;
            intervals[index - 1] = (samples[index].Timestamp - samples[index - 1].Timestamp) * toMs;
            errors[index - 1] = Math.Abs(intervals[index - 1] - intervalMs);
        }
        double mean = intervals.Average();
        double deviation = Math.Sqrt(intervals.Average(value => (value - mean) * (value - mean)));
        Console.WriteLine($"总周期样本数: {intervals.Length}");
        Console.WriteLine($"平均周期: {mean:F4} ms");
        Console.WriteLine($"标准差 (抖动): {deviation:F4} ms");
        Console.WriteLine($"最小 / 最大周期: {intervals.Min():F4} / {intervals.Max():F4} ms");
        Console.WriteLine($"P95 / P99 周期: {Percentile(intervals, 0.95):F4} / {Percentile(intervals, 0.99):F4} ms");
        Console.WriteLine($"P99 绝对周期误差: {Percentile(errors, 0.99):F4} ms");
        Console.WriteLine($"P99.9 / 最大绝对周期误差: {Percentile(errors, 0.999):F4} / {errors.Max():F4} ms");
        Console.WriteLine($"|周期误差| > 0.1 / 0.5 / 1 ms: " +
            $"{errors.Count(value => value > 0.1)} / {errors.Count(value => value > 0.5)} / {errors.Count(value => value > 1)} 次");
        Console.WriteLine($"|周期误差| > 0.1 / 0.5 / 1 ms 比例: " +
            $"{errors.Count(value => value > 0.1) * 100.0 / errors.Length:F4}% / " +
            $"{errors.Count(value => value > 0.5) * 100.0 / errors.Length:F4}% / " +
            $"{errors.Count(value => value > 1) * 100.0 / errors.Length:F4}%");
        Console.WriteLine($"周期 < 0.5 倍目标 / > 2 倍目标: " +
            $"{intervals.Count(value => value < intervalMs * 0.5)} / {intervals.Count(value => value > intervalMs * 2)} 次");
        Console.WriteLine($"最大采集间隔: {intervals.Max() / intervalMs:F2} 个标称周期（不代表已补回过去的样本）。");
        Console.WriteLine($"相对时间表的 P99 绝对偏差: {Percentile(phase.Select(Math.Abs).ToArray(), 0.99):F4} ms");
        Console.WriteLine($"相对时间表的最大绝对偏差: {phase.Max(Math.Abs):F4} ms");
        Console.WriteLine($"末次相位 - 首次相位: {phase[^1] - phase[0]:F4} ms");
        double fixedTimelineDrift = ((samples[count - 1].Timestamp - samples[0].Timestamp) -
            ((count - 1L) + skippedTotal - samples[0].Skipped) * period) * toMs;
        Console.WriteLine($"按原始周期推进的累计偏差（计入已统计的跳过时间点）: {fixedTimelineDrift:F4} ms");
        Console.WriteLine($"已跳过时间点: {skippedTotal}");
        Console.WriteLine($"调整时间表前 P99.9 / 最大唤醒迟到: " +
            $"{Percentile(wakeLateness, 0.999):F4} / {wakeLateness.Max():F4} ms");
        Console.WriteLine($"迟到后重排时间表次数: {scheduleResets}");
        Console.WriteLine($"进程 CPU 用量（含预热，100% 约等于一个逻辑核）: {cpuCorePercent:F1}%");
        Console.WriteLine($"测量及预热期间 Gen0 / Gen1 / Gen2 GC 次数: {string.Join(" / ", gcCollections)}（不能仅凭次数判断长延迟原因）。");

        var elapsedTimes = new double[count - 1];
        for (int index = 1; index < count; index++)
            elapsedTimes[index - 1] = (samples[index].Timestamp - samples[0].Timestamp) / (double)frequency;
        TemporalStatistics.Print(elapsedTimes, intervals, intervalMs, warmupSeconds, toleranceMs);

        if (output is not null)
        {
            string path = Path.GetFullPath(output);
            using var writer = new StreamWriter(path);
            writer.WriteLine("index,elapsed_ms,period_ms,phase_error_ms,skipped_periods,wake_lateness_ms,schedule_reset");
            for (int index = 0; index < count; index++)
                writer.WriteLine(FormattableString.Invariant($"{index},{(samples[index].Timestamp - samples[0].Timestamp) * toMs:F6},{(index == 0 ? double.NaN : intervals[index - 1]):F6},{phase[index]:F6},{samples[index].Skipped},{samples[index].WakeLateness * toMs:F6},{samples[index].ScheduleReset}"));
            Console.WriteLine($"原始记录: {path}");
        }
    }

    private static double Percentile(double[] values, double percentile)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        // 最近秩定义：第 ceil(p*N) 个元素，其零基下标必须减 1。
        return sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
