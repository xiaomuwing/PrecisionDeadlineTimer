namespace PrecisionDeadlineTimer
{
    /// <summary>
    /// 主程序：运行 PrecisionDeadlineTimer 基准。可用 --help 查看参数。
    /// </summary>
    class Program
    {
        static Task<int> Main(string[] args) => TimerBenchmark.RunAsync(args);
    }
}
