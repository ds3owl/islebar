namespace IsleBar.Core.Ui;

/// <summary>
/// Turns four band levels (low, low-mid, high-mid, high — from <see cref="BandMeter"/>) into the four music bar heights,
/// so the bars follow the actual sound (optional setting). Heights are scales for the bar visual: <see cref="Floor"/> .. 1.0.
/// <para>
/// Each band is placed between its own recent quiet and loud envelopes (fast to follow a new extreme, slow to let go), so
/// the beat uses the whole bar height at any system volume — raw levels barely moved at low volume and dividing by the
/// recent maximum pinned the bars near the top (user feedback 09-30). A square curve keeps the average part of a song low
/// so the peaks stand out. Heights rise fast and fall slowly (attack 0.85 / release 0.35 per tick, the envelope Atoll uses).
/// </para>
/// <para>
/// The loud envelope lets go of a one-off burst (a notification sound, a loud hit) within about a second — at 1%/tick it
/// held the bars on the floor for several seconds after one, so they looked stuck (user report 10-08; measured on a 45 s
/// Spotify capture: 86–92% of the 3 s after a burst on the floor → 22–34%). The noise gate sits just above digital silence
/// so quiet playback still moves the high band (it was on the floor 72% of the time at 0.0005).
/// </para>
/// </summary>
public sealed class LevelBars
{
    public const float Floor = 0.2f;
    private const float Attack = 0.85f;
    private const float Release = 0.35f;
    private const float NoiseGate = 0.00002f;
    private const float LoudLetGo = 0.06f;    // per tick (30/s)
    private const float QuietLetGo = 0.02f;

    private readonly float[] _levels = new float[4];
    private readonly float[] _high = new float[4];
    private readonly float[] _low = [-1f, -1f, -1f, -1f];

    /// <summary>Current heights (after the last <see cref="Update"/>).</summary>
    public IReadOnlyList<float> Heights => _levels;

    /// <summary>One tick (about 30 a second): band levels → heights.</summary>
    public IReadOnlyList<float> Update(IReadOnlyList<float> bands)
    {
        for (var i = 0; i < _levels.Length; i++)
        {
            var v = i < bands.Count ? Math.Max(0f, bands[i]) : 0f;
            float level;
            if (v < NoiseGate)
            {
                level = 0f;
            }
            else
            {
                if (_low[i] < 0f)
                {
                    _low[i] = _high[i] = v;
                }

                _high[i] = v > _high[i] ? v : _high[i] + ((v - _high[i]) * LoudLetGo);
                _low[i] = v < _low[i] ? v : _low[i] + ((v - _low[i]) * QuietLetGo);
                var span = _high[i] - _low[i];
                level = span <= v * 0.05f ? 0.3f : Math.Clamp((v - _low[i]) / span, 0f, 1f);
                level *= level;
            }

            var target = Floor + (level * (1 - Floor));
            var step = target > _levels[i] ? Attack : Release;
            _levels[i] = Math.Clamp(_levels[i] + ((target - _levels[i]) * step), Floor, 1f);
        }

        return _levels;
    }
}
