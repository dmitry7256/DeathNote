using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Threading;

namespace DeathNote.Wpf.Services;

/// <summary>
/// Opt-in diagnostics only. Splits the synchronous UI action from dispatcher
/// drain, and correlates rare slow operations with process-wide GC activity.
/// Collection counts, allocations and pause duration include other threads;
/// they indicate correlation, not proof that the action caused a collection.
/// </summary>
public sealed class BenchmarkMeasurements
{
    public sealed record Sample(int Pass, double ActionMs, double DispatcherMs, double TotalMs,
        long AllocatedBytes, double GcPauseMs, int Gen0, int Gen1, int Gen2);

    private readonly Dictionary<string, List<Sample>> _samples = new(StringComparer.Ordinal);
    private readonly Dispatcher _dispatcher;
    public BenchmarkMeasurements(Dispatcher dispatcher) => _dispatcher=dispatcher;

    private readonly record struct GcSnapshot(long Allocated, TimeSpan Paused, int Gen0, int Gen1, int Gen2)
    {
        public static GcSnapshot Capture() => new(
            GC.GetTotalAllocatedBytes(precise:false),GC.GetTotalPauseDuration(),
            GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2));
    }

    public async Task IdleAsync() => await _dispatcher.InvokeAsync(
        () => { }, DispatcherPriority.ApplicationIdle);

    public async Task<Sample> MeasureAsync(string name, Action action, int pass = 0)
    {
        var beforeGc=GcSnapshot.Capture();
        long before=Stopwatch.GetTimestamp();
        action();
        long actionEnd=Stopwatch.GetTimestamp();
        await IdleAsync();
        long end=Stopwatch.GetTimestamp();
        var afterGc=GcSnapshot.Capture();
        var sample = new Sample(pass,
            ToMs(actionEnd-before),ToMs(end-actionEnd),ToMs(end-before),
            Math.Max(0,afterGc.Allocated-beforeGc.Allocated),
            Math.Max(0,(afterGc.Paused-beforeGc.Paused).TotalMilliseconds),
            afterGc.Gen0-beforeGc.Gen0, afterGc.Gen1-beforeGc.Gen1, afterGc.Gen2-beforeGc.Gen2);
        Add(name,sample);
        return sample;
    }

    public async Task<Sample> MeasureAwaitedAsync(string name, Func<Task> action, int pass = 0)
    {
        var beforeGc=GcSnapshot.Capture();
        long before=Stopwatch.GetTimestamp();
        await action();
        long actionEnd=Stopwatch.GetTimestamp();
        await IdleAsync();
        long end=Stopwatch.GetTimestamp();
        var afterGc=GcSnapshot.Capture();
        var sample=new Sample(pass,ToMs(actionEnd-before),ToMs(end-actionEnd),ToMs(end-before),
            Math.Max(0,afterGc.Allocated-beforeGc.Allocated),
            Math.Max(0,(afterGc.Paused-beforeGc.Paused).TotalMilliseconds),
            afterGc.Gen0-beforeGc.Gen0,afterGc.Gen1-beforeGc.Gen1,afterGc.Gen2-beforeGc.Gen2);
        Add(name,sample);
        return sample;
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private void Add(string name,Sample sample)
    {
        if(!_samples.TryGetValue(name,out var values))_samples.Add(name,values=[]);
        values.Add(sample);
    }

    public void AppendReport(StringBuilder report)
    {
        report.AppendLine("Each operation: action (handler/awaited I/O), dispatcher wait, total to ApplicationIdle.");
        report.AppendLine("GC deltas: gen0/gen1/gen2, process allocated bytes, accumulated stop-the-world pause (correlation only).");
        report.AppendLine("Percentiles are nearest-rank. A single cold sample has no meaningful p95.");
        report.AppendLine();
        foreach(var (name,samples) in _samples)
        {
            report.AppendLine($"{name}: n={samples.Count}, " +
                $"total p50={Pct(samples.Select(s=>s.TotalMs),50):F2} ms, " +
                $"p95={Pct(samples.Select(s=>s.TotalMs),95):F2} ms, " +
                $"max={samples.Max(s=>s.TotalMs):F2} ms; " +
                $"handler p50={Pct(samples.Select(s=>s.ActionMs),50):F2} ms, " +
                $"dispatcher p50={Pct(samples.Select(s=>s.DispatcherMs),50):F2} ms; " +
                $"GC events={samples.Count(s=>s.Gen0+s.Gen1+s.Gen2>0)}, " +
                $"GC pause sum={samples.Sum(s=>s.GcPauseMs):F2} ms; " +
                $"allocated sum={samples.Sum(s=>s.AllocatedBytes)/1024.0:F1} KiB");
            if(samples.Any(s=>s.Pass>0))
                foreach(var passGroup in samples.GroupBy(s=>s.Pass).OrderBy(g=>g.Key))
                    report.AppendLine($"  run {passGroup.Key}: n={passGroup.Count()}, " +
                        $"total p50={Pct(passGroup.Select(s=>s.TotalMs),50):F2}, " +
                        $"p95={Pct(passGroup.Select(s=>s.TotalMs),95):F2}, " +
                        $"max={passGroup.Max(s=>s.TotalMs):F2} ms; " +
                        $"GC 0/1/2={passGroup.Sum(s=>s.Gen0)}/{passGroup.Sum(s=>s.Gen1)}/{passGroup.Sum(s=>s.Gen2)}");
            if(samples.Count>1)
            {
                foreach(var slow in samples.OrderByDescending(s=>s.TotalMs).Take(3))
                    report.AppendLine($"  slow sample: run={slow.Pass} total={slow.TotalMs:F2} " +
                        $"handler={slow.ActionMs:F2} dispatcher={slow.DispatcherMs:F2} " +
                        $"gc0/1/2={slow.Gen0}/{slow.Gen1}/{slow.Gen2} " +
                        $"gcPause={slow.GcPauseMs:F2} ms alloc={slow.AllocatedBytes/1024.0:F1} KiB");
            }
        }
    }

    public static double Pct(IEnumerable<double> source,int percentile)
    {
        var values=source.Order().ToArray();
        if(values.Length==0)return 0;
        return values[Math.Clamp((int)Math.Ceiling(percentile/100.0*values.Length)-1,0,values.Length-1)];
    }
}
