using System.Globalization;
using System.Text;

namespace MovementSim;

// <summary>
// 仿真驱动：一次性输出任务要求的全部结果（基线曲线 / 参数扫描 / 窗口宽容度 / 能量饱和 / 滑铲衰减）。
// 只负责场景编排与 IO；所有算法在 MovementMath.cs。
// </summary>
internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string ResultsDir = "results";

    private static readonly double[] SweepMu = { 2, 4, 6, 8, 12 };
    private static readonly double[] SweepAccel = { 5, 10, 15, 20 };
    private static readonly double[] SweepAirTimeS = { 0.4, 0.7, 1.0 };
    private static readonly int[] DelayMs = { 0, 50, 100, 150, 200, 250 };
    private static readonly double[] SlideEnterSpeeds = { 0.8, 1.0, 1.5 };

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(ResultsDir);

        var baseline = new MovementParams();
        PrintHeader(baseline);
        SectionModelComparison(baseline);
        SectionBaseline(baseline);
        SectionEnergySaturation(baseline);
        SectionSweep(baseline);
        SectionJumpDelay(baseline);
        SectionSlide(baseline);
        PrintFiles();
    }

    private static void PrintHeader(MovementParams p)
    {
        Console.WriteLine("超高速行者——移动系统数学预研仿真（纯 C#，无第三方包）");
        Console.WriteLine($"  tick=1/{p.TickRate}s  wishspeed={p.WishSpeed}  accel={p.Accel}/s  mu={p.FrictionMu}/s  g={p.Gravity}  jumpVel={p.JumpVel}（滞空≈{2 * p.JumpVel / p.Gravity:F2}s）");
        Console.WriteLine($"  免摩擦窗口={p.FrictionFreeWindow}s  行走目标={p.WalkTarget}  行走趋近={p.WalkApproachRate}/s  滑铲衰减={p.SlideDecay}/s  能量上限={p.EnergyCap}");
        Console.WriteLine("  注意：以上全部为占位基线值（任务建议值），非最终数值，实装需手感和关卡验证后调整。");
    }

    // —— 场景 0：字面模型 vs 修正模型对照，证明偏差的必要性 ——
    private static void SectionModelComparison(MovementParams baseline)
    {
        Console.WriteLine();
        Console.WriteLine("== 0. 模型对照：任务公式字面模型 vs 修正模型（10 个跑-跳循环，其余参数同基线） ==");
        foreach (var mode in new[] { PumpMode.VerbatimQuake, PumpMode.WindowPump })
        {
            var p = baseline with { Pump = mode };
            var sim = RunUp(p);
            var jumps = new List<double>();
            RunCycles(sim, p, DelayTicksPerfectBhop, 10, jumps);
            Console.WriteLine($"  {mode,-14}: 第 1 次起跳 {jumps[0]:F4} → 第 10 次起跳 {jumps[^1]:F4}（阈值倍数）");
        }
        Console.WriteLine("  → 字面模型速度恒为 1.0（投影上限把地速钉死，只保速零增长），无法实现策划案『不断加速』。");
        Console.WriteLine("  → 以下所有仿真使用修正模型 WindowPump：仅免摩擦窗口内泵加速不受投影上限（详见 report.md）。");
    }

    // —— 场景 1：基线 50 循环增长曲线 ——
    private static void SectionBaseline(MovementParams baseline)
    {
        Console.WriteLine();
        Console.WriteLine($"== 1. 基线增长曲线：50 个跑-跳循环（起跳延迟 {DelayTicksPerfectBhop} tick = {DelayTicksPerfectBhop * 1000.0 / baseline.TickRate:F1}ms，理想 bhop） ==");
        var p = baseline;
        var sim = RunUp(p);
        long startTick = sim.TickIndex;
        var speeds = new List<double>();
        var energies = new List<double>();
        var phases = new List<string>();
        var jumpSpeeds = new List<double>();
        RunCycles(sim, p, DelayTicksPerfectBhop, 50, jumpSpeeds, s =>
        {
            speeds.Add(s.Speed);
            energies.Add(s.Energy);
            phases.Add(s.Grounded ? "Ground" : "Air");
        });

        Console.WriteLine($"  起跳速度：第 1 次 {jumpSpeeds[0]:F3} → 第 10 次 {jumpSpeeds[9]:F3} → 第 30 次 {jumpSpeeds[29]:F3} → 第 50 次 {jumpSpeeds[49]:F3}（×阈值）");
        double perCycle = jumpSpeeds[1] - jumpSpeeds[0];
        Console.WriteLine($"  单循环增速：+{perCycle:F4}（+{perCycle / p.GroundThreshold:P1} 阈值/循环，线性非复利）；全程无摩擦损耗（每次起跳都在窗口内）");
        PlotAscii("水平速度（每 tick 采样；'-' 行 = 地速阈值 1.0）", speeds, 64, 18, refLine: p.GroundThreshold, refLabel: "地速阈值");
        PlotAscii("矢量转换器能量（每 tick 采样；'-' 行 = 能量上限 200）", energies, 64, 12, yMax: p.EnergyCap, refLine: p.EnergyCap, refLabel: "能量上限");

        var sb = new StringBuilder("tick,speed,energy,phase\n");
        for (int i = 0; i < speeds.Count; i++)
            sb.Append($"{startTick + i + 1},{speeds[i].ToString("F6", Inv)},{energies[i].ToString("F6", Inv)},{phases[i]}\n");
        File.WriteAllText(Path.Combine(ResultsDir, "speed-curve.csv"), sb.ToString());
        Console.WriteLine("  已写出 results/speed-curve.csv（tick,speed,energy,phase）");
    }

    // —— 场景 2：能量饱和时刻 ——
    private static void SectionEnergySaturation(MovementParams baseline)
    {
        Console.WriteLine();
        Console.WriteLine("== 2. 能量饱和：基线参数延长仿真直至能量到 200 ==");
        var p = baseline;
        var sim = RunUp(p);
        long satTick = -1;
        double satSpeed = 0;
        int cycles = 0;
        long guard = (long)p.TickRate * 600;
        while (sim.TickIndex < guard)
        {
            bool jump = sim.Grounded && sim.GroundTicks >= DelayTicksPerfectBhop;
            if (jump) cycles++;
            sim.Tick(Locomotion.Run, jump, false);
            if (sim.Energy >= p.EnergyCap - 1e-9)
            {
                satTick = sim.TickIndex;
                satSpeed = sim.Speed;
                break;
            }
        }
        if (satTick > 0)
            Console.WriteLine($"  能量到 200：第 {satTick} tick（{(double)satTick / p.TickRate:F1}s 游戏时间，约第 {cycles} 个跑-跳循环），对应速度 {satSpeed:F3}×阈值");
        else
            Console.WriteLine("  600s 内未饱和（增速过慢，需要重新选参）");
    }

    // —— 场景 3：参数扫描 ——
    private static void SectionSweep(MovementParams baseline)
    {
        Console.WriteLine();
        Console.WriteLine("== 3. 参数扫描：30 个跑-跳循环后的速度（阈值倍数） ==");
        Console.WriteLine("  判定：停滞 <5%/循环 ≤ 推荐 ≤15%/循环 < 偏快 ≤25%/循环 < 过快（按阈值占比的线性增速）；* = 第 30 循环速度 > 2.5×阈值");
        var csv = new StringBuilder("mu,accel,airTimeS,jumpDelayMs,speed30,gainPerCycle,verdict,over2p5\n");

        // 主网格：理想 bhop 节奏（1 tick 起跳延迟），任务规定的 mu × accel × 滞空时长
        foreach (var airT in SweepAirTimeS)
        {
            double jumpVel = baseline.Gravity * airT / 2.0; // 滞空时长 ≈ 2*JumpVel/Gravity，g 固定调 jumpVel
            Console.WriteLine();
            Console.WriteLine($"  滞空 {airT:F1}s（g={baseline.Gravity:F0}, jumpVel={jumpVel:F1}，1 tick 起跳）  列=accel / 行=mu");
            Console.WriteLine("    mu\\accel " + string.Join("", SweepAccel.Select(a => $"|{a,7:F0}    ")));
            foreach (var mu in SweepMu)
            {
                var cells = new StringBuilder();
                foreach (var accel in SweepAccel)
                {
                    var prm = baseline with { FrictionMu = mu, Accel = accel, JumpVel = jumpVel };
                    double s30 = SpeedAfterCycles(prm, DelayTicksPerfectBhop, 30);
                    double gain = (s30 - baseline.GroundThreshold) / 30.0;
                    string verdict = VerdictOf(gain);
                    bool over = s30 > 2.5 * baseline.GroundThreshold;
                    cells.Append($"|{s30,7:F2}{(over ? "*" : " ")} {VerdictChar(verdict)}  ");
                    csv.Append($"{mu.ToString(Inv)},{accel.ToString(Inv)},{airT.ToString(Inv)},{DelayTicksPerfectBhop * 1000.0 / baseline.TickRate:F1},{s30.ToString("F4", Inv)},{gain.ToString("F6", Inv)},{verdict},{over}\n");
                }
                Console.WriteLine($"    {mu,8:F0} " + cells + "|");
            }
        }
        Console.WriteLine("  → 主网格结论：增速只由 accel 决定（每地面 tick 泵 +accel/60 阈值）；mu 与滞空时长在窗口内起跳时完全不进入循环。");

        // 附加块：松弛节奏（250ms > 免摩擦窗口 200ms），摩擦进入循环，mu 开始起作用
        const int slackDelayTicks = 15;
        Console.WriteLine();
        Console.WriteLine($"  附加：起跳延迟 {slackDelayTicks} tick（{slackDelayTicks * 1000.0 / baseline.TickRate:F0}ms > 窗口 200ms，松弛节奏）同网格  列=accel / 行=mu");
        Console.WriteLine("    mu\\accel " + string.Join("", SweepAccel.Select(a => $"|{a,7:F0}    ")));
        foreach (var mu in SweepMu)
        {
            var cells = new StringBuilder();
            foreach (var accel in SweepAccel)
            {
                var prm = baseline with { FrictionMu = mu, Accel = accel };
                double s30 = SpeedAfterCycles(prm, slackDelayTicks, 30);
                double gain = (s30 - baseline.GroundThreshold) / 30.0;
                string verdict = VerdictOf(gain);
                bool over = s30 > 2.5 * baseline.GroundThreshold;
                cells.Append($"|{s30,7:F2}{(over ? "*" : " ")} {VerdictChar(verdict)}  ");
                csv.Append($"{mu.ToString(Inv)},{accel.ToString(Inv)},{0.7.ToString(Inv)},{slackDelayTicks * 1000.0 / baseline.TickRate:F0},{s30.ToString("F4", Inv)},{gain.ToString("F6", Inv)},{verdict},{over}\n");
            }
            Console.WriteLine($"    {mu,8:F0} " + cells + "|");
        }
        Console.WriteLine("  → 附加块结论：只有起跳晚于窗口时 mu 才起作用；『高 mu + 低 accel』出现停滞区，『低 mu + 高 accel』爆炸。");

        File.WriteAllText(Path.Combine(ResultsDir, "param-sweep.csv"), csv.ToString());
        Console.WriteLine("  已写出 results/param-sweep.csv（mu,accel,airTimeS,jumpDelayMs,speed30,gainPerCycle,verdict,over2p5）");
    }

    // —— 场景 4：落地窗口宽容度 ——
    private static void SectionJumpDelay(MovementParams baseline)
    {
        Console.WriteLine();
        Console.WriteLine("== 4. 落地窗口宽容度：起跳延迟对 30 循环后速度的影响（基线参数） ==");
        Console.WriteLine("  延迟ms   tick   30循环后速度   单循环增速   结论");
        var csv = new StringBuilder("delayMs,delayTicks,speed30,gainPerCycle,verdict\n");
        foreach (var delayMs in DelayMs)
        {
            int delayTicks = (int)Math.Round(delayMs * baseline.TickRate / 1000.0);
            double s30 = SpeedAfterCycles(baseline, delayTicks, 30);
            double gain = (s30 - baseline.GroundThreshold) / 30.0;
            string verdict = gain <= 1e-9 ? "零增长（纯保速）" : $"保持增长 +{gain:P1}/循环";
            Console.WriteLine($"  {delayMs,6}  {delayTicks,4}  {s30,12:F3}  {gain,10:P1}  {verdict}");
            csv.Append($"{delayMs},{delayTicks},{s30.ToString("F4", Inv)},{gain.ToString("F6", Inv)},{verdict}\n");
        }
        File.WriteAllText(Path.Combine(ResultsDir, "jump-delay.csv"), csv.ToString());
        Console.WriteLine("  → 全部 0-250ms 延迟都不掉速（最差也是保速）；≥1 tick 即有增长；窗口内延迟越长泵油越多，");
        Console.WriteLine("    超过窗口后被摩擦平衡封顶。窗口极宽松，无需帧级操作——但『晚跳更快』与 bhop 直觉相反，见 report.md 风险节。");
        Console.WriteLine("  已写出 results/jump-delay.csv");
    }

    // —— 场景 5：滑铲衰减 ——
    private static void SectionSlide(MovementParams baseline)
    {
        Console.WriteLine();
        Console.WriteLine($"== 5. 滑铲衰减：进入速度 → 速度 <{baseline.SlideExitSpeed} 所需时间（指数衰减 {baseline.SlideDecay}/s） ==");
        var csv = new StringBuilder("enterSpeed,ticks,timeS,slideDistance\n");
        foreach (var v0 in SlideEnterSpeeds)
        {
            var sim = new MovementSimulator(baseline);
            sim.ForceGroundSpeed(v0);
            double d0 = sim.Distance;
            int ticks = 0;
            long guard = baseline.TickRate * 60;
            while (ticks < guard)
            {
                sim.Tick(Locomotion.None, false, slideHeld: true);
                ticks++;
                if (!sim.Sliding && sim.Speed < baseline.SlideExitSpeed) break;
            }
            double time = ticks / (double)baseline.TickRate;
            Console.WriteLine($"  进入 {v0:F1} → <{baseline.SlideExitSpeed}：{ticks} tick（{time:F3}s），滑铲距离 {sim.Distance - d0:F3}（无量纲，米换算待尺度参数确定）");
            csv.Append($"{v0.ToString(Inv)},{ticks},{time.ToString("F4", Inv)},{(sim.Distance - d0).ToString("F4", Inv)}\n");
        }
        File.WriteAllText(Path.Combine(ResultsDir, "slide.csv"), csv.ToString());
        Console.WriteLine("  已写出 results/slide.csv（enterSpeed,ticks,timeS,slideDistance）");
    }

    private static void PrintFiles()
    {
        Console.WriteLine();
        Console.WriteLine("== 输出文件 ==");
        foreach (var f in Directory.GetFiles(ResultsDir).OrderBy(f => f))
            Console.WriteLine($"  {Path.GetFullPath(f)}");
        Console.WriteLine("  结论报告：results/report.md");
    }

    // —— 驱动工具 —— //

    // 理想 bhop 节奏：落地后留 1 个地面 tick 奔跑结算，再起跳（0 tick = 同 tick 立即起跳，无泵油）
    private const int DelayTicksPerfectBhop = 1;

    // 助跑：行走趋近 WalkTarget，再奔跑加速到地速阈值后进入跳循环（此时早已脱离免摩擦窗口）
    private static MovementSimulator RunUp(MovementParams p)
    {
        var sim = new MovementSimulator(p);
        int guard = 0;
        while (sim.Speed < p.WalkTarget - 1e-9 && guard++ < p.TickRate * 10)
            sim.Tick(Locomotion.Walk, false, false);
        while (sim.Speed < p.GroundThreshold - 1e-9 && guard++ < p.TickRate * 20)
            sim.Tick(Locomotion.Run, false, false);
        return sim;
    }

    // 跑-跳循环：始终按住奔跑；落地后先跑 delayTicks 个地面 tick，再起跳。jumpSpeeds 记录每次起跳携带的速度
    private static void RunCycles(MovementSimulator sim, MovementParams p, int delayTicks, int cycles,
        List<double>? jumpSpeeds = null, Action<MovementSimulator>? onTick = null)
    {
        long guard = (long)p.TickRate * 1200;
        int done = 0;
        while (done < cycles && sim.TickIndex < guard)
        {
            bool jump = sim.Grounded && sim.GroundTicks >= delayTicks;
            if (jump)
            {
                jumpSpeeds?.Add(sim.Speed);
                done++;
            }
            sim.Tick(Locomotion.Run, jump, false);
            onTick?.Invoke(sim);
        }
    }

    private static double SpeedAfterCycles(MovementParams p, int delayTicks, int cycles)
    {
        var sim = RunUp(p);
        var jumps = new List<double>();
        RunCycles(sim, p, delayTicks, cycles, jumps);
        return jumps[^1];
    }

    private static string VerdictOf(double gainPerCycle) => gainPerCycle switch
    {
        < 0.05 => "停滞",
        <= 0.15 => "推荐",
        <= 0.25 => "偏快",
        _ => "过快",
    };

    private static string VerdictChar(string verdict) => verdict switch
    {
        "停滞" => "S",
        "推荐" => "R",
        "偏快" => "f",
        _ => "F",
    };

    // —— ASCII 折线图：列分桶取峰值，行代表量级，'-' 行为参考线 ——
    private static void PlotAscii(string title, IReadOnlyList<double> ys, int width, int height,
        double? yMax = null, double? refLine = null, string? refLabel = null)
    {
        Console.WriteLine();
        Console.WriteLine($"  [{title}]");
        double top = yMax ?? (ys.Count > 0 ? ys.Max() : 1.0);
        if (top <= 0) top = 1.0;
        int n = ys.Count;
        var bucket = new double[width];
        for (int c = 0; c < width; c++)
        {
            int lo = (int)((long)c * n / width);
            int hi = Math.Max((int)((long)(c + 1) * n / width), lo + 1);
            double m = double.MinValue;
            for (int i = lo; i < hi && i < n; i++) m = Math.Max(m, ys[i]);
            bucket[c] = m;
        }
        int refRow = refLine is double rl ? Math.Clamp(height - (int)Math.Round(height * rl / top), 0, height - 1) : -1;
        for (int r = 0; r < height; r++)
        {
            double level = top * (height - r) / height;
            var line = new StringBuilder($"{level,8:F2} |");
            for (int c = 0; c < width; c++)
                line.Append(bucket[c] >= level ? '*' : r == refRow ? '-' : ' ');
            if (r == refRow) line.Append($"  ← {refLabel}");
            Console.WriteLine(line);
        }
        Console.WriteLine($"         +{new string('-', width)}");
        Console.WriteLine($"          0 → {n} tick（{(double)n / 60.0:F1}s），每列 ≈ {Math.Max(1, n / width)} tick");
    }
}
