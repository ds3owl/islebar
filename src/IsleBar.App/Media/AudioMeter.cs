using System.Runtime.InteropServices;
using IsleBar.Core.Ui;

namespace IsleBar.App.Media;

/// <summary>
/// Band levels of what the PC is playing, for the music bars ("music bars follow the sound", optional).
/// <para>
/// Uses WASAPI loopback on the default speakers: the same mixed output that goes to the speakers is handed to us as
/// samples (the microphone is not involved, nothing is stored). A background thread feeds them to Core's
/// <see cref="BandMeter"/>; <see cref="Levels"/> returns the four band RMS values since the last call. The speaker peak meter
/// was tried first but modern music sits at full scale, so it showed no beat (measured 09-30).
/// </para>
/// Never throws; if there are no speakers or the format isn't float, <see cref="Levels"/> just returns zeros.
/// </summary>
internal sealed class AudioMeter : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _needed = new(false);
    private long _lastUseTicks;
    private BandMeter? _meter;
    private Thread? _thread;

    /// <summary>Stop capturing after the bars have gone this long unread (music stopped), so the audio hardware can idle.</summary>
    private static readonly TimeSpan ParkAfter = TimeSpan.FromSeconds(4);

    /// <summary>Band RMS values (low, low-mid, high-mid, high) since the last call. Starts capture on first use.</summary>
    public float[] Levels()
    {
        Volatile.Write(ref _lastUseTicks, DateTime.UtcNow.Ticks);
        _needed.Set();   // if capture had parked (no music for a while), wake it back up
        if (_thread is null)
        {
            _thread = new Thread(Capture) { IsBackground = true, Name = "IsleBar loopback" };
            _thread.Start();
        }

        lock (_gate)
        {
            return _meter?.TakeLevels() ?? new float[4];
        }
    }

    private DateTime LastUse() => new(Volatile.Read(ref _lastUseTicks), DateTimeKind.Utc);

    private void Capture()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                _needed.Wait(_stop.Token);   // parked: sleep (no loopback, speakers can power down) until the bars are read again
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                CaptureOnce();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                // device changed or unplugged — try again shortly
            }

            _stop.Token.WaitHandle.WaitOne(300);
        }
    }

    private void CaptureOnce()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0 || device is null)
        {
            return;
        }

        var iid = typeof(IAudioClient).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 1, IntPtr.Zero, out var clientObject));
        var client = (IAudioClient)clientObject;
        Marshal.ThrowExceptionForHR(client.GetMixFormat(out var formatPointer));
        try
        {
            var channels = Marshal.ReadInt16(formatPointer, 2);
            var sampleRate = Marshal.ReadInt32(formatPointer, 4);
            var bits = Marshal.ReadInt16(formatPointer, 14);
            if (bits != 32 || channels <= 0)
            {
                return;   // shared-mode mix format is float32 on Windows 10/11; anything else is left alone
            }

            const int loopback = 0x00020000;   // AUDCLNT_STREAMFLAGS_LOOPBACK
            Marshal.ThrowExceptionForHR(client.Initialize(0 /* shared */, loopback, 200_000 /* 20 ms */, 0, formatPointer, IntPtr.Zero));
            var captureIid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref captureIid, out var captureObject));
            var capture = (IAudioCaptureClient)captureObject;
            lock (_gate)
            {
                _meter = new BandMeter(sampleRate);
            }

            Marshal.ThrowExceptionForHR(client.Start());
            try
            {
                var buffer = new float[4096];
                while (!_stop.IsCancellationRequested)
                {
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out var packet));
                    while (packet > 0)
                    {
                        Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                        var silent = (flags & 0x2) != 0;   // AUDCLNT_BUFFERFLAGS_SILENT
                        var count = frames * channels;
                        if (buffer.Length < count)
                        {
                            buffer = new float[count];
                        }

                        if (!silent)
                        {
                            Marshal.Copy(data, buffer, 0, count);
                        }

                        lock (_gate)
                        {
                            for (var f = 0; f < frames; f++)
                            {
                                float mono = 0;
                                if (!silent)
                                {
                                    for (var c = 0; c < channels; c++)
                                    {
                                        mono += buffer[(f * channels) + c];
                                    }

                                    mono /= channels;
                                }

                                _meter!.Add(mono);
                            }
                        }

                        Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(frames));
                        Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out packet));
                    }

                    // Nothing has read the bars for a while (music stopped) → release the loopback and park.
                    if (DateTime.UtcNow - LastUse() > ParkAfter)
                    {
                        _needed.Reset();
                        if (DateTime.UtcNow - LastUse() > ParkAfter)
                        {
                            lock (_gate)
                            {
                                _meter = null;   // don't hand back stale levels after resuming
                            }

                            return;   // falls through to Stop()/release; the next Levels() sets _needed and we re-activate
                        }

                        _needed.Set();   // a read slipped in while we were resetting — keep capturing
                    }

                    _stop.Token.WaitHandle.WaitOne(10);
                }
            }
            finally
            {
                client.Stop();
                Marshal.ReleaseComObject(capture);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(formatPointer);
            Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _needed.Set();   // release the thread if it's parked so it observes cancellation
        _thread?.Join(TimeSpan.FromSeconds(1));
        _needed.Dispose();
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint frames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint padding);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);

        [PreserveSig]
        int GetMixFormat(out IntPtr format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr handle);

        [PreserveSig]
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(int frames);

        [PreserveSig]
        int GetNextPacketSize(out int frames);
    }
}
