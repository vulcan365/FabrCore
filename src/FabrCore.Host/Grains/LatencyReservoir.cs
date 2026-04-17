namespace FabrCore.Host.Grains
{
    /// <summary>
    /// Ring-buffer reservoir of recent message-processing latencies (in ms).
    /// Single grain activation is single-threaded (non-reentrant by default) so
    /// no locking is needed for writes; the snapshot copy in <see cref="Snapshot"/>
    /// is taken atomically via array copy.
    /// </summary>
    internal sealed class LatencyReservoir
    {
        private readonly double[] _samples;
        private int _count;
        private int _writeIndex;

        public LatencyReservoir(int capacity = 256)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _samples = new double[capacity];
        }

        public int Count => _count;

        public void Record(double latencyMs)
        {
            _samples[_writeIndex] = latencyMs;
            _writeIndex = (_writeIndex + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;
        }

        /// <summary>
        /// Returns percentile-ready sorted snapshot. Null if empty.
        /// </summary>
        public double[]? Snapshot()
        {
            if (_count == 0) return null;
            var copy = new double[_count];
            Array.Copy(_samples, 0, copy, 0, _count);
            Array.Sort(copy);
            return copy;
        }

        /// <summary>
        /// Linear-interpolation percentile (0-100). Expects <paramref name="sorted"/>
        /// to be a sorted snapshot from <see cref="Snapshot"/>.
        /// </summary>
        public static double Percentile(double[] sorted, double percentile)
        {
            if (sorted.Length == 0) return 0;
            if (sorted.Length == 1) return sorted[0];

            var rank = (percentile / 100.0) * (sorted.Length - 1);
            var lower = (int)Math.Floor(rank);
            var upper = (int)Math.Ceiling(rank);
            if (lower == upper) return sorted[lower];

            var weight = rank - lower;
            return sorted[lower] * (1 - weight) + sorted[upper] * weight;
        }
    }
}
