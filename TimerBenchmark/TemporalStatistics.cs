namespace PrecisionDeadlineTimer;

/// <summary>仅在线程退出后分析，保留逐周期数据，不在采集线程里排序或打印。</summary>
internal static class TemporalStatistics
{
    internal readonly record struct Window(double Start, double End, int Count, double Min,
        double Max, double P99Error, int Violations);

    internal static Window Summarize(double[] endTimes, double[] intervals,
        double start, double end, double targetMs, double toleranceMs)
    {
        var values = new List<double>();
        for (int index = 0; index < endTimes.Length; index++)
            if (endTimes[index] >= start && endTimes[index] < end) values.Add(intervals[index]);
        if (values.Count == 0) return new Window(start, end, 0, double.NaN, double.NaN, double.NaN, 0);
        double[] errors = values.Select(value => Math.Abs(value - targetMs)).Order().ToArray();
        return new Window(start, end, values.Count, values.Min(), values.Max(),
            errors[(int)Math.Ceiling(errors.Length * 0.99) - 1], errors.Count(value => value > toleranceMs));
    }

    // 返回最后一个超限周期的结束时间；其后的尾段只在本次观察范围内无超限。
    internal static double? LastViolation(double[] endTimes, double[] intervals,
        double targetMs, double toleranceMs)
    {
        for (int index = intervals.Length - 1; index >= 0; index--)
            if (Math.Abs(intervals[index] - targetMs) > toleranceMs) return endTimes[index];
        return null;
    }

    internal static void Print(double[] endTimes, double[] intervals, double targetMs,
        double warmupSeconds, double toleranceMs)
    {
        if (intervals.Length == 0) return;
        Console.WriteLine($"\n--- 分时稳定性：每个完整周期须在 {targetMs - toleranceMs:F3}～{targetMs + toleranceMs:F3} ms 内 ---");
        Console.WriteLine(warmupSeconds == 0
            ? "时间从首个回调算起，包含启动阶段；首次触发延迟未计入相邻周期。"
            : $"时间从首个保留样本算起，启动前 {warmupSeconds:F2} 秒已排除；诊断预热请使用 --warmup-seconds 0。");
        Console.WriteLine("时间段(s)    周期数    最小(ms)    最大(ms)    P99绝对误差(ms)    超限次数");
        double observedEnd = endTimes[^1];
        double[] earlyEdges = [0, 0.1, 0.2, 0.5, 1, 2, 5, 10];
        for (int index = 1; index < earlyEdges.Length; index++)
            PrintWindow(earlyEdges[index - 1], earlyEdges[index]);
        for (double start = 10; start < observedEnd; start += 10)
            PrintWindow(start, Math.Min(start + 10, observedEnd + 1e-9));

        double? lastViolation = LastViolation(endTimes, intervals, targetMs, toleranceMs);
        if (lastViolation.HasValue)
        {
            double tail = observedEnd - lastViolation.Value;
            Console.WriteLine($"最后一个超限周期结束于 {lastViolation:F4} s；之后连续无超限仅观察了 {tail:F4} s。");
            Console.WriteLine(tail >= 60
                ? "本次出现至少 60 秒连续无超限的尾段；单次尾段不能确定预热时长，仍需重复冷启动比较。"
                : "没有观察到至少 60 秒连续无超限的尾段，不能给出可靠的预热后持续稳定时间。");
        }
        else Console.WriteLine($"本次全部 {intervals.Length} 个完整周期通过，覆盖 {observedEnd:F4} s；这仅描述本次测试。");

        int worst = 0;
        for (int index = 1; index < intervals.Length; index++)
            if (Math.Abs(intervals[index] - targetMs) > Math.Abs(intervals[worst] - targetMs)) worst = index;
        Console.WriteLine($"最大绝对偏差对应周期 {intervals[worst]:F4} ms，结束于 {endTimes[worst]:F4} s。");

        void PrintWindow(double start, double end)
        {
            if (start > observedEnd) return;
            end = Math.Min(end, observedEnd + 1e-9);
            Window window = Summarize(endTimes, intervals, start, end, targetMs, toleranceMs);
            Console.WriteLine($"{start,6:F1}～{end,6:F1}  {window.Count,6}    {window.Min,8:F4}    {window.Max,8:F4}    {window.P99Error,12:F4}    {window.Violations,6}");
        }
    }
}
