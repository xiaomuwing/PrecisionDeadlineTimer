# PrecisionDeadlineTimer 使用说明

固定时间表的软实时定时器（.NET，Windows 优化）。Windows 10 1803+ 用
`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` 高分辨率等待定时器睡到截止点前，最后一段忙等守点；
时间表锚定在启动时刻（第 N 次截止点 = 起始时刻 + N × 周期），抖动不累积，长期零漂移；
超期的时间点只计数、不补发。

## 适用边界

| 场景 | 判定 |
| --- | --- |
| 周期 ≥ 2 ms，允许偶发可观测的超期（容差 ≥ ±1 ms）：慢信号轮询、趋势记录、周期任务 | ✅ 推荐 |
| 周期 1~2 ms | ⚠️ 边缘：忙等占空比约 50%，CPU 代价高 |
| 周期 < 1 ms（>1 kHz） | ❌ 等待定时器粒度 0.5 ms，调度尖峰超过整个周期 |
| 要求每周期 ±0.5 ms 硬保证 | ❌ P99.9 尾部来自操作系统抢占，软件无法消除 |
| 等间隔波形采样、相位测量、多通道同步 | ❌ 软件回调间隔 ≠ 采样时刻，用设备硬件采样时钟 |

长期运行注意：相对自身时间表零漂移；相对 UTC 存在晶振 ppm 误差（约 ±12~60 秒/周，
恒定速率，可标定补偿）。QPC 为单调时钟，NTP 校时和系统改时间不影响它。

## 快速开始

```csharp
using var cts = new CancellationTokenSource();

// 200 ms 周期示例：窗口取周期的 7.5%
var timer = new PrecisionDeadlineTimer(
    spinWindow: TimeSpan.FromMilliseconds(15),
    useMmcss: true);

var task = Task.Factory.StartNew(
    () => timer.Run(OnTick, TimeSpan.FromMilliseconds(200), cts.Token),
    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

// 停止：cts.Cancel(); await task;

void OnTick(TimerTick tick)
{
    // 只做采集和入队；界面、写盘、日志交给其他线程
    if (tick.SkippedPeriods > 0) { /* 有超期，记录或告警 */ }
}
```

`Run` 在调用线程上阻塞运行，取消后返回；务必用 `TaskCreationOptions.LongRunning` 给它独立线程。
回调中抛出的异常沿 Run 的调用线程传出。

## 构造参数

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `spinWindow` | 1.5 ms | 忙等窗口上限。CPU ≈ 窗口 ÷ 周期；经验公式 `clamp(周期 × 7.5%, 2 ms, 15 ms)` |
| `latePolicy` | `KeepSchedule` | `KeepSchedule`：迟到后下一周期缩短以重新对齐，零漂移（推荐）；`Rebase`：迟到超阈值就以当前时刻重排，抑制短周期但时间表漂移 |
| `rebaseAfter` | 0.1 ms | Rebase 策略的重排阈值；KeepSchedule 下无效 |
| `useMmcss` | true | 注册 MMCSS "Pro Audio" 调度类并解除 Win11 后台进程定时器节流；失败静默退化 |
| `minSpinWindow` | = spinWindow | 设得更小则窗口自适应收缩省电（CPU 可降到 2~4%），代价是 P99 精度降到 0.5~2.5 ms 量级 |

窗口为什么钳在 2~15 ms：小于约 2 ms 会因等待定时器 0.5 ms 粒度加唤醒抖动而频繁"晚醒出窗"；
大于约 15 ms 无收益——更大的迟到是线程被抢占，忙等救不回来，只能靠 MMCSS 从调度层面压制。

## 回调编写规范

回调耗时计入周期预算：工作做多少，等待就减多少。安全边界：

```
回调最大耗时 ≈ 周期 − spinWindow − 0.5~1 ms 抖动余量
```

- 必须短小：不打印、不写盘、不阻塞、不等待锁
- 避免分配内存：GC 暂停直接变成唤醒迟到
- 重活（解析、存储、网络）入队给工作线程，并监控队列容量
- 工作逼近预算上限时先崩的是送达率（跳过计数上升），时间表漂移仍为零；工作超过周期时
  进入降频模式（每周期跳过整数个点），不补发、不积压

## 监控指标（TimerTick）

时间戳单位均为 tick，除以 `Stopwatch.Frequency` 换算为秒。

| 字段 | 含义 | 用法 |
| --- | --- | --- |
| `Timestamp` | 实际触发时刻 | 相位偏差 = Timestamp − ScheduledTimestamp |
| `ScheduledTimestamp` | 对应的时间表时刻 | 同上 |
| `SkippedPeriods` | 自上次回调以来跳过的整周期数 | > 0 即告警：回调超预算或系统尖峰 |
| `WakeLatenessTicks` | 调整时间表前的唤醒迟到 | 观测尾部尖峰，不被跳过逻辑隐藏 |
| `ScheduleReset` | 本次是否执行了 Rebase | 仅 Rebase 策略下可能为 true |

## 调试注意事项

| 症状 | 可能原因 | 处理 |
| --- | --- | --- |
| 长周期（数百 ms）下出现 2.5~11.5 ms 离散尖峰 | Win11 对后台进程的 EcoQoS 定时器合并 | `useMmcss: true`（已自动解除）；`timeBeginPeriod` 单独调用无效 |
| P99 突然变差、跳过点持续上升 | 回调耗时逼近预算上限 | 按上面的公式核算；瘦身回调 |
| 首几秒误差大 | JIT 编译与冷启动 | 预热后统计，或忽略启动段 |
| CPU 偏高 | 窗口占空比 = 窗口 ÷ 周期 | 长周期按比例放宽窗口即可接受；短周期用 `minSpinWindow` 自适应 |
| 取消后 Run 不返回 | 回调正在执行阻塞操作 | 取消只能打断等待，不能中断已进入的回调 |
| 系统睡眠/重启后时间线异常 | 设计行为 | 睡眠期算作跳过周期并计数，唤醒后回到原时间表 |

验证与复测：项目的 Main 直接运行内置基准 `TimerBenchmark`（编译后执行
`PrecisionDeadlineTimer.exe --help` 查看参数）。关键参数：`--interval-ms`、`--spin-ms`、
`--min-spin-ms`、`--mmcss`、`--work-ms`（模拟回调负载）、`--warmup-seconds`、`--output`。
观测时看双侧指标：P99/P99.9 绝对周期误差、相对时间表相位偏差、唤醒迟到、跳过点数、CPU。

## 实测性能参考（本机 Windows 11，2026-10，Release，MMCSS 开）

200 ms 周期、15 ms 窗口、Normal 优先级、预热 20 秒测 100 秒：
平均 200.0000 ms，标准差 0.0118 ms，P99 绝对周期误差 0.0635 ms，最大 0.1287 ms，
100 秒漂移 −0.0007 ms，跳过 0，CPU 7.7%。

满核负载（32 线程占满全部逻辑核）下同配置：P99 0.0304 ms，最大 0.0318 ms；
无 MMCSS 对照组劣化到 P99 0.2400 ms——MMCSS 是满核场景的关键保护。

回调预算（5 ms 周期、1.5 ms 窗口）：工作 2 ms 时 P99 0.006 ms、零漂移；
工作 6 ms（超期）时降频为 10 ms 触发、3000 个跳过点精确计数、漂移 0.0000 ms。

以上数字是本机软实测定性参考，不构成工业验收；上线前用真实采集任务在代表性负载下长测。
