using QL.World.Services;

namespace QL.World.Tests;

/// <summary>
/// M5-H: verifies the sliding-window weak-network detector. Key invariants:
/// (1) below MIN_SAMPLES the flag never flips; (2) >50% failure rate trips it;
/// (3) >3000ms avg latency trips it; (4) recovery (successes displace the
/// failures) clears it; (5) Changed fires only on transition, not per sample.
/// </summary>
public sealed class NetworkConditionMonitorTests
{
    [Fact]
    public void Empty_monitor_is_not_weak()
    {
        var monitor = new NetworkConditionMonitor();
        Assert.False(monitor.IsWeakNetwork);
    }

    [Fact]
    public void Fewer_than_min_samples_never_flips_to_weak()
    {
        // MIN_SAMPLES = 3; two failures alone must not trip the flag.
        var monitor = new NetworkConditionMonitor();
        monitor.RecordFailure();
        monitor.RecordFailure();
        Assert.False(monitor.IsWeakNetwork);
    }

    [Fact]
    public void Failure_rate_above_threshold_trips_weak()
    {
        // 3 samples, 2 failures → 66% > 50% threshold.
        var monitor = new NetworkConditionMonitor();
        monitor.RecordFailure();
        monitor.RecordFailure();
        monitor.RecordSuccess(TimeSpan.FromMilliseconds(100));
        Assert.True(monitor.IsWeakNetwork);
    }

    [Fact]
    public void High_avg_latency_trips_weak()
    {
        // 3 successes but each >3s → avg > 3000ms threshold.
        var monitor = new NetworkConditionMonitor();
        var slow = TimeSpan.FromMilliseconds(3500);
        monitor.RecordSuccess(slow);
        monitor.RecordSuccess(slow);
        monitor.RecordSuccess(slow);
        Assert.True(monitor.IsWeakNetwork);
    }

    [Fact]
    public void Mixed_load_below_threshold_stays_healthy()
    {
        // 3 successes at 100ms each → 0% failures, low latency.
        var monitor = new NetworkConditionMonitor();
        var fast = TimeSpan.FromMilliseconds(100);
        monitor.RecordSuccess(fast);
        monitor.RecordSuccess(fast);
        monitor.RecordSuccess(fast);
        Assert.False(monitor.IsWeakNetwork);
    }

    [Fact]
    public void Recovery_clears_weak_flag()
    {
        // Trip the flag with 3 failures, then displace them with successes.
        var monitor = new NetworkConditionMonitor();
        monitor.RecordFailure();
        monitor.RecordFailure();
        monitor.RecordFailure();
        Assert.True(monitor.IsWeakNetwork);

        // WINDOW_SIZE = 10; push 10 successes to fully displace the failures.
        var fast = TimeSpan.FromMilliseconds(50);
        for (int i = 0; i < 10; i++)
            monitor.RecordSuccess(fast);
        Assert.False(monitor.IsWeakNetwork);
    }

    [Fact]
    public void Changed_event_fires_only_on_transitions()
    {
        var monitor = new NetworkConditionMonitor();
        int fired = 0;
        monitor.Changed += () => fired++;

        // Trip → fire once.
        monitor.RecordFailure();
        monitor.RecordFailure();
        monitor.RecordFailure();
        Assert.Equal(1, fired);
        Assert.True(monitor.IsWeakNetwork);

        // More failures while already weak → no extra fires.
        monitor.RecordFailure();
        Assert.Equal(1, fired);

        // Recover → fire once.
        var fast = TimeSpan.FromMilliseconds(50);
        for (int i = 0; i < 10; i++)
            monitor.RecordSuccess(fast);
        Assert.Equal(2, fired);
        Assert.False(monitor.IsWeakNetwork);
    }

    [Fact]
    public void Sliding_window_evicts_oldest_samples()
    {
        // 3 failures → weak. Then 10 successes displace them (window size 10).
        // Followed by a single failure: only 1/10 = 10% < 50% → recovered.
        var monitor = new NetworkConditionMonitor();
        monitor.RecordFailure();
        monitor.RecordFailure();
        monitor.RecordFailure();
        Assert.True(monitor.IsWeakNetwork);

        var fast = TimeSpan.FromMilliseconds(50);
        for (int i = 0; i < 10; i++)
            monitor.RecordSuccess(fast);
        Assert.False(monitor.IsWeakNetwork);

        // One more failure shouldn't trip — only 1 failure in window of 10.
        monitor.RecordFailure();
        Assert.False(monitor.IsWeakNetwork);
    }
}
