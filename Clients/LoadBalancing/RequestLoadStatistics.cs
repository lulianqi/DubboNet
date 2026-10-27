using System;
using System.Diagnostics;
using System.Threading;

namespace DubboNet.Clients.LoadBalancing
{
    // Consumer-local, per-provider/per-method observations, independent of the wire protocol.
    internal sealed class RequestLoadStatistics
    {
        internal const int WindowSeconds = 30;
        internal const double ProbeIntervalMilliseconds = 5000;
        private struct Bucket
        {
            internal long Second, Successes, Failures;
            internal double SuccessMilliseconds;
        }

        private readonly object _sync = new();
        private readonly Bucket[] _buckets = new Bucket[WindowSeconds];
        private int _active;
        private double _lastProbe = double.NegativeInfinity;

        internal static double MonotonicMilliseconds() => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);
        internal int Active => Volatile.Read(ref _active);
        internal void Begin() => Interlocked.Increment(ref _active);
        internal void MarkProbe(double now) { lock (_sync) _lastProbe = now; }

        internal void End(double now, double elapsed, bool success)
        {
            try
            {
                lock (_sync)
                {
                    long second = (long)(now / 1000);
                    int index = (int)(second % WindowSeconds);
                    if (_buckets[index].Second != second) _buckets[index] = new Bucket { Second = second };
                    if (success)
                    {
                        _buckets[index].Successes++;
                        _buckets[index].SuccessMilliseconds += Math.Max(0.001, elapsed);
                    }
                    else _buckets[index].Failures++;
                }
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        internal Snapshot Read(double now)
        {
            lock (_sync)
            {
                long second = (long)(now / 1000);
                long successes = 0, failures = 0;
                double milliseconds = 0;
                foreach (Bucket bucket in _buckets)
                {
                    if (bucket.Second <= second && second - bucket.Second < WindowSeconds)
                    {
                        successes += bucket.Successes;
                        failures += bucket.Failures;
                        milliseconds += bucket.SuccessMilliseconds;
                    }
                }
                return new Snapshot(Volatile.Read(ref _active), successes, failures, milliseconds, _lastProbe);
            }
        }

        internal readonly struct Snapshot
        {
            internal readonly int Active;
            internal readonly long Successes, Failures;
            internal readonly double SuccessMilliseconds, LastProbe;
            internal bool HasSamples => Successes + Failures > 0;
            internal double AverageMilliseconds => Successes == 0 ? 0 : SuccessMilliseconds / Successes;

            internal Snapshot(int active, long successes, long failures, double milliseconds, double lastProbe)
            { Active = active; Successes = successes; Failures = failures; SuccessMilliseconds = milliseconds; LastProbe = lastProbe; }

            internal double EstimateResponse(double failurePenaltyMilliseconds) =>
                (SuccessMilliseconds + Failures * failurePenaltyMilliseconds) / Math.Max(1, Successes + Failures) * (Active + 1d);
        }
    }
}
