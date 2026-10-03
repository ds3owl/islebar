namespace IsleBar.Core.Ui;

/// <summary>
/// Splits audio samples into four frequency bands (low, low-mid, high-mid, high) and reports each band's RMS level —
/// what the music bars follow when "music bars follow the sound" is on. Peak meters can't do this: modern mastered music
/// sits at full scale almost all the time (measured 09-30: the speaker peak stayed pinned at ~0.067), so only band
/// energy shows the beat. Plain biquad filters, the same idea Atoll uses (low-pass 150 Hz, band-passes, high-pass).
/// Feed mono samples with <see cref="Add"/>; <see cref="TakeLevels"/> returns the RMS per band since the last take.
/// </summary>
public sealed class BandMeter
{
    private readonly Biquad[] _filters;
    private readonly double[] _sumSquares = new double[4];
    private int _count;

    public BandMeter(int sampleRate)
    {
        _filters =
        [
            Biquad.LowPass(sampleRate, 150, 0.707),
            Biquad.BandPass(sampleRate, 500, 0.8),
            Biquad.BandPass(sampleRate, 2000, 0.8),
            Biquad.HighPass(sampleRate, 6000, 0.707),
        ];
    }

    public void Add(float sample)
    {
        for (var i = 0; i < _filters.Length; i++)
        {
            var y = _filters[i].Process(sample);
            _sumSquares[i] += y * y;
        }

        _count++;
    }

    /// <summary>RMS of each band since the last call (zeros if no samples came in), then starts over.</summary>
    public float[] TakeLevels()
    {
        var levels = new float[4];
        if (_count > 0)
        {
            for (var i = 0; i < 4; i++)
            {
                levels[i] = (float)Math.Sqrt(_sumSquares[i] / _count);
                _sumSquares[i] = 0;
            }
        }

        _count = 0;
        return levels;
    }

    /// <summary>Second-order filter (RBJ audio cookbook), direct form I.</summary>
    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1, _x2, _y1, _y2;

        public double Process(double x)
        {
            var y = (b0 * x) + (b1 * _x1) + (b2 * _x2) - (a1 * _y1) - (a2 * _y2);
            (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
            return y;
        }

        public static Biquad LowPass(int rate, double freq, double q)
        {
            var (cos, alpha) = Params(rate, freq, q);
            var a0 = 1 + alpha;
            return new Biquad((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        public static Biquad HighPass(int rate, double freq, double q)
        {
            var (cos, alpha) = Params(rate, freq, q);
            var a0 = 1 + alpha;
            return new Biquad((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        public static Biquad BandPass(int rate, double freq, double q)
        {
            var (cos, alpha) = Params(rate, freq, q);
            var a0 = 1 + alpha;
            return new Biquad(alpha / a0, 0, -alpha / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        private static (double Cos, double Alpha) Params(int rate, double freq, double q)
        {
            var w0 = 2 * Math.PI * Math.Min(freq, rate * 0.45) / rate;
            return (Math.Cos(w0), Math.Sin(w0) / (2 * q));
        }
    }
}
