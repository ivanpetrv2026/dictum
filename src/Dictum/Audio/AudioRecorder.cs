using System.IO;
using System.Threading;
using Dictum.Services;
using NAudio.Wave;

namespace Dictum.Audio;

/// <summary>
/// Captures microphone audio as raw 16 kHz / 16-bit / mono PCM — the format
/// Whisper expects. Uses WinMM (WaveInEvent): no WASAPI loopback, nothing that
/// resembles audio interception.
///
/// Device selection resolves a saved *name* against the current WinMM list and
/// falls back to the system default when that device disappeared. A watchdog
/// names stalled streams and digital silence instead of letting a take fail
/// silently. DICTUM_AUDIO_FIXTURE replaces the device with a WAV file so the
/// capture path can be exercised without a working microphone.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private System.Threading.Timer? _watchdog;
    private Thread? _fixtureThread;
    private volatile bool _fixtureRunning;
    private readonly object _bufferGate = new();

    private long _lastDataTicks;
    private int _silentBuffers;
    private bool _stalled;
    private bool _digitalSilence;

    public bool IsRecording => _waveIn is not null || _fixtureRunning;

    /// <summary>Product name of the device used for the last recording.</summary>
    public string DeviceName { get; private set; } = "неизвестное устройство";

    public bool IsFixture { get; private set; }

    public DeviceResolution? Resolution { get; private set; }

    /// <summary>True when the last take stopped receiving microphone data.</summary>
    public bool LastTakeStalled => _stalled;

    /// <summary>True when the last take delivered long runs of exact zeros.</summary>
    public bool LastTakeDigitallySilent => _digitalSilence;

    public void Start(int deviceNumber = 0, int sampleRate = 16000)
        => Start(null, deviceNumber, sampleRate);

    public void Start(string? preferredName, int deviceNumber = 0, int sampleRate = 16000)
    {
        if (IsRecording)
        {
            return;
        }

        _stalled = false;
        _digitalSilence = false;
        _silentBuffers = 0;
        IsFixture = false;
        Resolution = null;
        _buffer = new MemoryStream();

        var fixturePath = Environment.GetEnvironmentVariable("DICTUM_AUDIO_FIXTURE");
        if (!string.IsNullOrWhiteSpace(fixturePath))
        {
            StartFixture(fixturePath);
            return;
        }

        var names = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try
            {
                names.Add(WaveInEvent.GetCapabilities(i).ProductName);
            }
            catch (Exception ex)
            {
                names.Add($"device {i} (unreadable: {ex.Message})");
            }
        }

        var resolution = DeviceResolver.Resolve(names, preferredName, deviceNumber);
        Resolution = resolution;

        if (!resolution.IsUsable)
        {
            throw new InvalidOperationException(
                "Нет доступных устройств захвата звука. Подключите микрофон и повторите диктовку.");
        }

        DeviceName = $"{resolution.Index}: {resolution.Name}";
        Log.Write($"audio: resolved {resolution.Reason} index={resolution.Index} name='{resolution.Name}' ({resolution.Detail})");
        Diagnostics.Write("capture", "device-resolved", 0,
            ("device_index", resolution.Index),
            ("reason", resolution.Reason.ToString()),
            ("fallback", resolution.IsFallback));

        _lastDataTicks = DateTime.UtcNow.Ticks;
        _waveIn = new WaveInEvent
        {
            DeviceNumber = resolution.Index,
            WaveFormat = new WaveFormat(sampleRate, 16, 1),
            BufferMilliseconds = 50,
            NumberOfBuffers = 4
        };
        _waveIn.DataAvailable += OnDataAvailable;

        try
        {
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            Log.Write("audio: device failed to open: " + ex.Message);
            Diagnostics.Write("capture", "open-failed", 0, ("reason_code", "open_failed"));
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.Dispose();
            _waveIn = null;
            throw;
        }

        // Start() always opens a fresh WaveInEvent after Stop() disposed the old
        // one, so a stalled device is renegotiated once on the next press.
        _watchdog = new System.Threading.Timer(CheckStall, null, 1000, 1000);
    }

    private void StartFixture(string path)
    {
        var (source, error) = FixtureSource.TryLoad(path);
        if (source is null)
        {
            throw new InvalidOperationException($"DICTUM_AUDIO_FIXTURE: {error} ({path})");
        }

        IsFixture = true;
        DeviceName = $"fixture: {Path.GetFileName(path)} ({source.DurationMs} ms)";
        Log.Write($"audio: fixture '{path}' {source.DurationMs} ms, {source.ChunkCount} chunks, no device opened");
        Diagnostics.Write("capture", "fixture-started", 0, ("duration_ms", source.DurationMs));

        _fixtureRunning = true;
        _fixtureThread = new Thread(() =>
        {
            for (var i = 0; _fixtureRunning && i < source.ChunkCount; i++)
            {
                var chunk = source.Chunk(i);
                lock (_bufferGate)
                {
                    _buffer?.Write(chunk, 0, chunk.Length);
                }

                Volatile.Write(ref _lastDataTicks, DateTime.UtcNow.Ticks);
                Thread.Sleep(10);
            }

            _fixtureRunning = false;
        })
        {
            IsBackground = true,
            Name = "dictum-audio-fixture"
        };
        _fixtureThread.Start();
    }

    private void CheckStall(object? state)
    {
        if (_waveIn is null || _stalled)
        {
            return;
        }

        var last = new DateTime(Volatile.Read(ref _lastDataTicks), DateTimeKind.Utc);
        var idle = DateTime.UtcNow - last;
        if (idle <= TimeSpan.FromSeconds(4))
        {
            return;
        }

        _stalled = true;
        Log.Write("audio: no microphone data for 4 s — stream stalled; device reopens on the next press");
        Diagnostics.Write("capture", "stream-stalled", (long)idle.TotalMilliseconds,
            ("idle_ms", (long)idle.TotalMilliseconds));
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        Volatile.Write(ref _lastDataTicks, DateTime.UtcNow.Ticks);
        var count = e.BytesRecorded;
        if (count <= 0)
        {
            return;
        }

        var allZero = true;
        for (var i = 0; i < count; i++)
        {
            if (e.Buffer[i] != 0)
            {
                allZero = false;
                break;
            }
        }

        if (allZero)
        {
            if (++_silentBuffers == 20)
            {
                _digitalSilence = true;
                Log.Write("audio: digital silence — device is delivering exact zeros (stale clock or released device)");
                Diagnostics.Write("capture", "digital-silence", 0, ("buffers", _silentBuffers));
            }
        }
        else
        {
            _silentBuffers = 0;
        }

        lock (_bufferGate)
        {
            _buffer?.Write(e.Buffer, 0, count);
        }
    }

    public byte[] Stop()
    {
        _watchdog?.Dispose();
        _watchdog = null;

        if (_fixtureThread is not null)
        {
            _fixtureRunning = false;
            _fixtureThread.Join(TimeSpan.FromSeconds(2));
            _fixtureThread = null;
        }

        if (_waveIn is not null)
        {
            try
            {
                _waveIn.StopRecording();
            }
            catch (NAudio.MmException)
            {
                // Device disappeared mid-recording; keep whatever was captured.
                Log.Write("audio: device disappeared mid-recording; keeping captured audio");
            }

            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.Dispose();
            _waveIn = null;
        }

        byte[] data;
        lock (_bufferGate)
        {
            data = _buffer?.ToArray() ?? Array.Empty<byte>();
            _buffer?.Dispose();
            _buffer = null;
        }

        return data;
    }

    public void Dispose() => Stop();
}
