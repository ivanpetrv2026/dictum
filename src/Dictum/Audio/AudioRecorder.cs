using System.IO;
using Dictum.Services;
using NAudio.Wave;

namespace Dictum.Audio;

/// <summary>
/// Captures microphone audio as raw 16 kHz / 16-bit / mono PCM — the format
/// Whisper expects. Uses WinMM (WaveInEvent): no WASAPI loopback, no device
/// enumeration, nothing that resembles audio interception.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;

    public bool IsRecording => _waveIn is not null;

    public void Start(int deviceNumber = 0)
    {
        if (_waveIn is not null)
        {
            return;
        }

        _buffer = new MemoryStream();

        var selectedDevice = deviceNumber < 0 ? 0 : deviceNumber;
        var deviceName = WaveInEvent.GetCapabilities(selectedDevice).ProductName;
        Log.Write($"audio: device {selectedDevice} = '{deviceName}'");

        _waveIn = new WaveInEvent
        {
            DeviceNumber = selectedDevice,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50,
            NumberOfBuffers = 4
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
    }

    public byte[] Stop()
    {
        if (_waveIn is null)
        {
            return Array.Empty<byte>();
        }

        try
        {
            _waveIn.StopRecording();
        }
        catch (NAudio.MmException)
        {
            // Device disappeared mid-recording; keep whatever was captured.
        }

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.Dispose();
        _waveIn = null;

        var data = _buffer?.ToArray() ?? Array.Empty<byte>();
        _buffer?.Dispose();
        _buffer = null;
        return data;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _buffer?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    public void Dispose() => Stop();
}
