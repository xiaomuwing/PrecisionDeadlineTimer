# PrecisionDeadlineTimer

跨平台高精度软实时定时器（.NET 10）。固定时间表语义：时间表锚定在启动时刻
（第 N 次截止点 = 起始时刻 + N × 周期），抖动不累积，长期零漂移；超期的时间点只计数、不补发。
Windows 用 `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` 高分辨率等待定时器睡到截止点前、最后一段忙等守点；
Linux 用 timerfd(CLOCK_MONOTONIC) 内核周期定时 + 可选忙等窗口 + 可选 SCHED_FIFO 实时调度。

实测精度（10~200 ms 周期）：裸机 Linux P99 达 0.0015 ms，Windows 满载 P99 0.006 ms，
详见下文"实测性能参考"与"双平台同机对比"。

## 项目结构

| 项目 | 类型 | 内容 |
| --- | --- | --- |
| `CrossPlatformTimer/` | .NET 10 类库（`CrossPlatformTimer.dll`，命名空间 `PrecisionDeadlineTimer`） | 三个定时器类：`CrossPlatformTimer`（跨平台封装，推荐入口）、`PrecisionDeadlineTimer`（Windows 引擎）、`LinuxNativeTimer`（Linux 引擎） |
| `TimerBenchmark/` | .NET 10 控制台基准/测试程序 | 引用类库，双引擎共用的采样、统计、CSV 输出管线 |

```
dotnet build PrecisionDeadlineTimer.slnx -c Release
TimerBenchmark/bin/Release/net10.0/TimerBenchmark --help
```

## 适用边界

| 场景 | 判定 |
| --- | --- |
| 周期 ≥ 2 ms，允许偶发可观测的超期（容差 ≥ ±1 ms）：慢信号轮询、趋势记录、周期任务 | ✅ 推荐 |
| 周期 1~2 ms | ⚠️ 分平台：Windows 引擎受 0.5 ms 等待粒度限制，忙等占空比约 50%；Linux 引擎 + SCHED_FIFO 实测 1 kHz 可用（P99.9 0.12 ms，见"Linux 引擎"实测表） |
| 周期 < 1 ms（>1 kHz） | ❌ 等待定时器粒度 0.5 ms，调度尖峰超过整个周期 |
| 要求每周期 ±0.5 ms 硬保证 | ❌ P99.9 尾部来自操作系统抢占，软件无法消除 |
| 等间隔波形采样、相位测量、多通道同步 | ❌ 软件回调间隔 ≠ 采样时刻，用设备硬件采样时钟 |

长期运行注意：相对自身时间表零漂移；相对 UTC 存在晶振 ppm 误差（约 ±12~60 秒/周，
恒定速率，可标定补偿）。QPC（Windows）与 CLOCK_MONOTONIC（Linux）均为单调时钟，NTP 校时和系统改时间不影响它们。

## 快速开始

引用 `CrossPlatformTimer` 类库后，推荐用跨平台封装作为入口（Linux 自动走 timerfd 引擎，
Windows 自动走忙等引擎）：

```csharp
using PrecisionDeadlineTimer;
using var cts = new CancellationTokenSource();

// 200 ms 周期示例：显式使用 15 ms 忙等窗口，需按目标机器复测；rtPriority 仅 Linux 生效
var timer = new CrossPlatformTimer(
    spinWindow: TimeSpan.FromMilliseconds(15),
    rtPriority: 50);

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

只在 Windows 上跑、需要 MMCSS / 自适应窗口等 Windows 专属能力时，可直接用 `PrecisionDeadlineTimer`
（参数见下表）；只跑 Linux 且要显式控制 timerfd 行为时用 `LinuxNativeTimer`。

`Run` 在调用线程上阻塞运行，取消后返回；务必用 `TaskCreationOptions.LongRunning` 给它独立线程。
回调中抛出的异常沿 Run 的调用线程传出。

## 构造参数（Windows 引擎 PrecisionDeadlineTimer）

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `spinWindow` | 1.5 ms | 忙等窗口上限，实际不超过周期；按目标机器的唤醒延迟与 CPU 预算复测选取，见下文 |
| `latePolicy` | `KeepSchedule` | `KeepSchedule`：迟到后下一周期缩短以重新对齐，零漂移（推荐）；`Rebase`：迟到超阈值就以当前时刻重排，抑制短周期但时间表漂移 |
| `rebaseAfter` | 0.1 ms | Rebase 策略的重排阈值；KeepSchedule 下无效 |
| `useMmcss` | true | 注册 MMCSS "Pro Audio" 调度类并解除 Win11 后台进程定时器节流；失败静默退化 |
| `minSpinWindow` | = spinWindow | 等于上限时窗口固定；设得更小则自适应收缩以减少忙等，精度和 CPU 变化取决于机器与负载 |

`interval-ms` 指采样周期 T，`spin-ms` 指截止点前的忙等窗口上限 S。Windows 引擎将
S 限制为不超过 T，计划在截止点前 S 毫秒结束系统等待，再忙等到截止点；实际返回时刻受调度影响。
省略 `--min-spin-ms` 时窗口固定；显式设置更小的下限时，实际窗口会在上下限之间调整。

单定时线程、回调工作很少且按时提前唤醒时，理论忙等 CPU 占用约为 `min(S, T) / T`，
其中 100% 表示一个逻辑核。实际占用还受等待返回延迟、回调耗时和其他开销影响。
当 S 不小于 T 时，空闲等待部分趋于全程忙等，仍不能避免被系统中断或其他任务抢占。

旧经验公式 `clamp(T × 7.5%, 2 ms, 15 ms)` 的含义是将窗口限制在 2～15 ms，
它是历史参数试选方法，不是精度保证，也不是代码自动计算窗口的规则：

| 周期 T | 旧公式窗口 | 理论忙等 CPU（一个逻辑核） |
| ---: | ---: | ---: |
| 5 ms | 2 ms | 40% |
| 20 ms | 2 ms | 10% |
| 100 ms | 7.5 ms | 7.5% |
| 1000 ms | 15 ms | 1.5% |

窗口主要用于留出系统等待晚返回的余量；这类延迟不与采样周期保持固定比例。
因此，不能用周期的固定百分比推导 ±0.1 ms 精度，也不能断言小于 2 ms 一定不够、
大于 15 ms 一定无收益。提高窗口对已经进入忙等后的抢占也不能提供最坏延迟保证。

修正基准的 Windows 优先级设置顺序后，本机 MMCSS Critical + 固定 1.5 ms 窗口，
20 ms 周期测 100 秒、5 ms 周期测 30 秒均未观察到超过 0.1 ms 的周期误差；
20 ms 周期另一次 20 秒诊断中，将窗口从 1.5 增至 5 ms，CPU 从 4.9% 增至 22.5%，
最大周期误差分别为 0.0610 和 0.0632 ms，未观察到精度改善。
这些有限时长、无模拟回调工作的结果支持从 1.5 ms 开始复测，不证明它对所有周期或机器最优。

选取窗口时先固定 MMCSS 配置和回调负载，再比较不同窗口的 P99.9、最大周期误差、
最大相位误差、超限与跳过次数、CPU；保留达到实际验收要求的较小窗口。
库的默认窗口为 1.5 ms，基准程序省略 `--spin-ms` 时默认为 15 ms；两者均不自动应用旧公式。

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
| CPU 偏高 | 理论忙等占空比约为窗口 ÷ 周期 | 在满足实测精度的前提下缩小窗口；使用 `minSpinWindow` 自适应后需重新验证尾部误差 |
| 取消后 Run 不返回 | 回调正在执行阻塞操作 | 取消只能打断等待，不能中断已进入的回调 |
| 系统睡眠/重启后时间线异常 | 设计行为 | 睡眠期算作跳过周期并计数，唤醒后回到原时间表 |

验证与复测：用 `TimerBenchmark` 控制台程序（结构见上文"项目结构"）。关键参数：`--interval-ms`、`--spin-ms`、
`--min-spin-ms`、`--mmcss`、`--engine`、`--rt-priority`、`--work-ms`（模拟回调负载）、`--warmup-seconds`、`--output`。
观测时看双侧指标：P99/P99.9 绝对周期误差、相对时间表相位偏差、唤醒迟到、跳过点数、CPU。

## Linux 引擎（LinuxNativeTimer）

`LinuxNativeTimer` 是 Linux 原生定时引擎：timerfd(CLOCK_MONOTONIC) 周期定时 + eventfd 取消 +
poll 等待，与 `PrecisionDeadlineTimer` 完全相同的 `Run(workAction, interval, token)` 契约和
TimerTick 语义。基准程序用 `--engine auto|spin|linux` 选择引擎（`auto`：Linux 上默认 linux，
其余平台默认 spin），采样、统计、CSV 输出管线两引擎共用。

```bash
# Linux 上实测 timerfd 引擎（auto 已默认选中，此处显式指定）
./TimerBenchmark --engine linux --interval-ms 10 --seconds 60 --warmup-seconds 5
```

与忙等引擎的差异：

- 定时器在 Run 开始时才武装（timerfd_settime），时间表原点 = Run 起始时刻；
- 取消由 CancellationToken 回调写 eventfd 完成，定时循环在调用线程上，无独立读取线程；
- 错过周期由 timerfd 过期计数精确上报为 SkippedPeriods，不补发；
- P/Invoke 全部使用 IntPtr/UIntPtr 尺寸类型，32/64 位 Linux 布局均正确；
- 忙等窗口（`--spin-ms`，默认 0 = 纯内核等待）：timerfd 提前一个窗口唤醒，随后 SpinWait 守到截止点；
  实时调度（`--rt-priority 1~99`，默认 0 = 关）：定时线程提升为 SCHED_FIFO，需 root 或
  CAP_SYS_NICE（sudo 运行，或在 /etc/security/limits.conf 配置 rtprio），失败静默退化并打印警告。
  `--min-spin-ms` / `--mmcss` 对该引擎无效。

实测参考（WSL2 Ubuntu 24.04，2026-10，Release，10 ms 周期，30~60 秒）：

| 配置 | 负载 | P99 误差 | P99.9 误差 | 最大迟到 | 跳过点 | CPU |
| --- | --- | --- | --- | --- | --- | --- |
| 纯内核等待 | 空载 | 0.575 ms | 0.928 ms | 5.6 ms | 14/60 s | 0.2% |
| 忙等 1.5 ms | 空载 | 0.052 ms | 1.82 ms | 5.7 ms | 0 | 8.5% |
| 忙等 1.5 ms | 12 线程打满 | 0.006 ms | 61.4 ms | 212.6 ms | 46/30 s | 8.7% |
| 忙等 1.5 ms + SCHED_FIFO 50 | 12 线程打满 | 0.016 ms | 0.33 ms | 0.72 ms | 0 | ≈9% |
| spin 引擎对照 | 空载 | 0.049 ms | 3.82 ms | 8.6 ms | 0 | 93.4% |
| 1 kHz：忙等 0.3 ms + SCHED_FIFO 50 | 空载 | 0.047 ms | 0.42 ms | 1.2 ms | 42/30 s* | 12.8% |
| 1 kHz：忙等 0.3 ms + SCHED_FIFO 50 | 12 线程打满 | 0.042 ms | 0.12 ms | 0.82 ms | 3/30 s | 8.1% |
| **裸机 Linux**：忙等 15 ms + SCHED_FIFO 50（200 ms 周期，100 s） | 空载 | **0.0015 ms** | **0.0024 ms** | **0.0008 ms** | 0 | 7.4% |

裸机 Linux 是这套引擎的真实上限：P99 1.5 µs、标准差 500 ns、100 秒漂移 −0.0001 ms、
CPU 7.4% 与理论占空比 7.5% 精确吻合；同参数比裸机 Windows 11（P99 0.0635 ms）还准约 40 倍。

*1 kHz 空载组的 42 个跳过点来自一次 Hyper-V 约 38 ms 的停顿（WSL2 虚拟化层的随机尖峰，裸机少见）；
其余 1 kHz 指标均已进入 0.1 ms 内，1 kHz 在 Linux 引擎下可用。

结论：忙等窗口把 P99 压到 0.05 ms 量级（与全程忙等的 spin 引擎持平，CPU 从 93% 降到 9%）；
SCHED_FIFO 解决的是满载尾部——P99.9 从 61 ms 压到 0.33 ms、零跳过。两者叠加即推荐配置。
WSL2 虚拟化环境下仍有 Hyper-V 调度停顿引入的偶发尖峰，裸机 Linux 会更好。

## 跨平台封装（CrossPlatformTimer）

`CrossPlatformTimer` 把两个引擎包成统一 API：Linux 自动路由到 `LinuxNativeTimer`，其余平台
路由到 `PrecisionDeadlineTimer`，调用方一份代码两平台通用。基准程序的 `--engine auto`
（默认）走的就是这层封装。

```csharp
using var cts = new CancellationTokenSource();

var timer = new CrossPlatformTimer(
    spinWindow: TimeSpan.FromMilliseconds(15),  // 两平台共用的忙等窗口
    rtPriority: 50);                            // 仅 Linux 生效（SCHED_FIFO）；Windows 静默忽略

var task = Task.Factory.StartNew(
    () => timer.Run(OnTick, TimeSpan.FromMilliseconds(200), cts.Token),
    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
```

参数分两类：平台共用的 `spinWindow`；平台专属的 `latePolicy` / `rebaseAfter` / `useMmcss` /
`minSpinWindow`（仅 Windows）与 `rtPriority`（仅 Linux）——专属参数在另一平台静默忽略、不抛异常。
注意两引擎默认值不同：省略 `spinWindow` 时 Windows 用 1.5 ms 忙等、Linux 用纯内核等待；
跨平台一致性要求高的场景请显式指定 `spinWindow`。`CrossPlatformTimer.EngineName` 可查询当前
实际路由到的引擎（"linux" / "spin"）。

## 双平台同机对比（2026-10，同一台机器，10 ms 周期、1.5 ms 窗口、测 30 秒）

两边各自开启本平台的调度增强（Windows 用 MMCSS，Linux 用 SCHED_FIFO 50）；两侧平均周期均为
10.0000 ms、全程零漂移、跳过点计数精确。Linux 侧运行于 WSL2 虚拟机。

| 场景 | 指标 | Windows 11 原生（spin + MMCSS） | Linux（linux + SCHED_FIFO，WSL2） |
| --- | --- | --- | --- |
| 空载 | P99 / P99.9 误差 | 0.28 / 3.6~7.0 ms | **0.075 / 0.20 ms** |
| 空载 | 最大唤醒迟到 | 5.6~8.8 ms | **1.05 ms** |
| 空载 | CPU | 12% | **7.5%** |
| 满载（12 线程） | P99 / P99.9 误差 | **0.006 / 0.063 ms** | 0.016 / 0.33 ms |
| 满载（12 线程） | 最大唤醒迟到 | **0.11 ms** | 0.72 ms |
| 满载（12 线程） | CPU | 13% | ≈9% |

关键发现：**Windows 满载反而比空载好约 40 倍**。空载时核停车（core parking）与深层 C-state
的退出延迟造成数 ms 级迟到，MMCSS 无法消除；满载时核心全程高频在线，唤醒即刻完成。
Linux 侧的 SCHED_FIFO 是硬抢占保证，空满载都稳定，且 WSL2 的 vCPU 由 Hyper-V 持续调度，
无核停车问题——空载下虚拟机里的 Linux 反而赢了裸机 Windows 一个量级。
推论：Windows 空载数字会随电源策略显著改善；Linux 数字在裸机上只会更好。

裸机复核（2026-10，用户实测）：同一引擎在裸机 Linux 上 200 ms 周期跑出 P99 0.0015 ms、
最大 0.0024 ms、零跳过、100 秒漂移 −0.0001 ms——比裸机 Windows 同参数（P99 0.0635 ms）
还准约 40 倍，证实 WSL2 的 2~4 ms 迟到完全是虚拟化层 vCPU 停车所致（同核保活实验可将
WSL2 最大唤醒迟到从 3.5 ms 压到 0.36 ms，进一步佐证）。**精度验收必须在裸机上进行。**

## 最佳实践

### Windows（spin 引擎，默认）

- 引擎无需显式选择（`auto` 在非 Linux 平台自动选 spin）；MMCSS 保持默认开启，
  它会同时解除 Win11 对后台进程的定时器节流；
- 忙等窗口可从固定 1.5 ms 开始复测，根据实际唤醒延迟和 CPU 预算调整；
  5 ms 与 20 ms 周期已有本机测试，其他周期需另测。省电场景用 `minSpinWindow` 自适应后重新验收；
- **空载/间歇负载必须处理电源管理**（本次实测的关键结论）：
  `powercfg /setactive SCHEME_MIN` 切到高性能电源计划（高性能计划默认禁用核停车），
  或接受空载时数 ms 级的偶发迟到；负载稳定的产线环境无此问题；
- 回调纪律见"回调编写规范"：短小、不分配、不阻塞；监控 SkippedPeriods 告警。

Windows 可通过 `MmcssPriority` 初始化属性选择 MMCSS 任务内的相对优先级。
默认 `Normal` 保持原配置；`High` / `Critical` 是需要在目标机器上比较的调度选项：

```csharp
var timer = new CrossPlatformTimer(spinWindow: TimeSpan.FromMilliseconds(1.5))
{
    MmcssPriority = WindowsMmcssPriority.Critical
};
```

基准程序支持 `--mmcss-priority normal|high|critical`，例如：

```powershell
dotnet run --project TimerBenchmark -c Release -- --engine spin --interval-ms 20 --spin-ms 1.5 --mmcss true --mmcss-priority critical --priority above --warmup-seconds 20 --seconds 100 --tolerance-ms 0.1 --output windows-critical.csv
```

该选项调用 `AvSetMmThreadPriority`，只调整注册的 "Pro Audio" 任务内相对优先级。
它依赖 MMCSS 注册和优先级设置成功，不改变进程优先级，也不提供最坏唤醒延迟保证。
`useMmcss: false` 时不生效；Linux 引擎忽略此属性。应同时比较 P99.9、最大误差、
超限次数和 CPU，避免只按 P99 选择配置。
[API 语义](https://learn.microsoft.com/en-us/windows/win32/api/avrt/nf-avrt-avsetmmthreadpriority)。

普通 `Thread.Priority` 应在调用 `Run` 前设置。不要在 MMCSS 已注册后的回调中再设为
`AboveNormal` / `Normal`：这会重设 Windows 的线程基础优先级。基准程序已经将 Windows
优先级设置移到 `Run` 前；历史基准版本在首个回调中设置，复测时请注明版本。

修正后的本机 Windows 复测（2026-10，20 ms 周期、1.5 ms 窗口、MMCSS Critical、
预热 20 秒测 100 秒）：P99 周期误差 0.0458 ms、P99.9 0.0870 ms、最大 0.0897 ms，
4999 个完整周期零超限（0.1 ms）、零跳过，最大相位 0.0914 ms，CPU 6.8%。
5 ms 周期补测 30 秒：最大周期误差 0.0821 ms、零超限、CPU 24.7%。
这些是本机无模拟回调工作下的有限时长结果。

随后用户同配置测量 580 秒，28999 个完整周期中有 22 个超过 0.1 ms（0.0759%），
最大周期误差 0.2734 ms、最大相位 0.2741 ms，首尾累计偏差 −0.0001 ms。
因此上述 100 秒零超限不能证明长期所有周期达标。本机进一步诊断也复现了在提前唤醒后、
忙等阶段发生的尖峰；加宽窗口和绑定性能核的短测均仍有越界。

### Linux（linux 引擎）

- 推荐配置：`--engine linux --spin-ms <周期 × 15~30%> --rt-priority 50`；
  纯内核等待（不开窗口）仅用于精度要求宽松（±1 ms）且极致省电的场景；
- SCHED_FIFO 权限三选一：sudo 运行；`/etc/security/limits.conf` 加 `<用户> - rtprio 50`；
  systemd 服务加 `LimitRTPRIO=50`。无权限时静默退化为普通调度（P99 不受损，满载尾部受损）；
- 进一步收尾部：`taskset -c N` 绑核；裸机可用内核参数 `isolcpus` 隔离专用核；
  <100 µs 级硬实时需求换 PREEMPT_RT 内核；
- 生产环境避免 WSL2/虚拟机：虚拟化层会注入不可控的数 ms~数十 ms 尖峰（本次观测到 38 ms
  级 Hyper-V 停顿）；必须在 VM 中运行时，监控 SkippedPeriods 并按其告警。

## 实测性能参考（本机 Windows 11，2026-10，Release，MMCSS 开）

200 ms 周期、15 ms 窗口、Normal 优先级、预热 20 秒测 100 秒：
平均 200.0000 ms，标准差 0.0118 ms，P99 绝对周期误差 0.0635 ms，最大 0.1287 ms，
100 秒漂移 −0.0007 ms，跳过 0，CPU 7.7%。

满核负载（32 线程占满全部逻辑核）下同配置：P99 0.0304 ms，最大 0.0318 ms；
无 MMCSS 对照组劣化到 P99 0.2400 ms——MMCSS 是满核场景的关键保护。

回调预算（5 ms 周期、1.5 ms 窗口）：工作 2 ms 时 P99 0.006 ms、零漂移；
工作 6 ms（超期）时降频为 10 ms 触发、3000 个跳过点精确计数、漂移 0.0000 ms。

以上数字是本机软实测定性参考，不构成工业验收；上线前用真实采集任务在代表性负载下长测。
