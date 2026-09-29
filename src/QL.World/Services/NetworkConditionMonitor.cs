namespace QL.World.Services;

/// <summary>
/// M5-H: Sliding-window network health tracker for remote (NAS) requests.
/// Records the outcome + latency of the last <see cref="WINDOW_SIZE"/> calls;
/// declares <see cref="IsWeakNetwork"/> when the failure rate exceeds 50 %
/// or the average latency exceeds 3 s. Raised <see cref="Changed"/> only
/// when the flag transitions (not on every sample).
///
/// Start-up guard: <see cref="MIN_SAMPLES"/> samples must accumulate before
/// the flag can flip to true, so a single failed handshake on a fresh start
/// doesn't block the entire gallery.
/// </summary>
public sealed class NetworkConditionMonitor
{
    private const int WINDOW_SIZE = 10;
    private const int MIN_SAMPLES = 3;
    private const double FAILURE_THRESHOLD = 0.5;
    private const double LATENCY_THRESHOLD_MS = 3000;

    private readonly object _gate = new();
    private readonly Queue<Sample> _samples = new();
    private bool _isWeak;

    /// <summary>True when recent failures/latency indicate a poor connection.</summary>
    public bool IsWeakNetwork
    {
        get { lock (_gate) return _isWeak; }
    }

    /// <summary>Raised (on the calling thread) when <see cref="IsWeakNetwork"/> flips.</summary>
    public event Action? Changed;

    /// <summary>
    /// Records a successful request. <paramref name="latency"/> is the
    /// round-trip time (e.g. from connector.OpenReadAsync start to stream end).
    /// </summary>
    public void RecordSuccess(TimeSpan latency)
    {
        AddSample(new Sample(true, latency.TotalMilliseconds));
    }

    /// <summary>Records a failed request (timeout, auth error, network error).</summary>
    public void RecordFailure()
    {
        AddSample(new Sample(false, 0));
    }

    private void AddSample(Sample sample)
    {
        bool changed;
        lock (_gate)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > WINDOW_SIZE)
                _samples.Dequeue();

            bool nextWeak = EvaluateLocked();
            if (nextWeak != _isWeak)
            {
                _isWeak = nextWeak;
                changed = true;
            }
            else
            {
                changed = false;
            }
        }
        if (changed)
            Changed?.Invoke();
    }

    private bool EvaluateLocked()
    {
        if (_samples.Count < MIN_SAMPLES)
            return false;

        int failures = 0;
        double totalLatency = 0;
        int successCount = 0;
        foreach (Sample s in _samples)
        {
            if (s.Success)
            {
                successCount++;
                totalLatency += s.LatencyMs;
            }
            else
            {
                failures++;
            }
        }

        double failureRate = (double)failures / _samples.Count;
        double avgLatency = successCount > 0 ? totalLatency / successCount : 0;

        return failureRate > FAILURE_THRESHOLD || avgLatency > LATENCY_THRESHOLD_MS;
    }

    private readonly record struct Sample(bool Success, double LatencyMs);
}
