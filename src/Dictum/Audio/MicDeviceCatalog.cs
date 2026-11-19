using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Dictum.Audio;

/// <summary>
/// A row in the microphone picker. Index is a WinMM device number; -1 means the
/// endpoint exists in Windows but cannot currently be opened for recording.
/// Keeping the unusable endpoints visible is deliberate: an empty dropdown gives
/// the user no clue whether the app is broken or the microphone is disabled.
/// </summary>
public sealed record MicDevice(int Index, string Name, bool Usable, string Note)
{
    public string Display => Usable ? Name : $"{Name} — {Note}";
}

/// <summary>
/// Enumerates capture devices for the first-run wizard. WinMM is the list that
/// WaveInEvent can actually open, so it always wins. When WinMM reports zero
/// devices we fall back to WASAPI (all states) purely to explain WHY: disabled,
/// unplugged and otherwise unavailable endpoints are shown with their state.
/// </summary>
public static class MicDeviceCatalog
{
    private const string WindowsHint =
        "Звук → Запись → «Показать отключённые устройства» → включите микрофон";

    public static string Hint => WindowsHint;

    public static IReadOnlyList<MicDevice> Enumerate()
    {
        var devices = new List<MicDevice>();

        try
        {
            var count = WaveInEvent.DeviceCount;
            for (var i = 0; i < count; i++)
            {
                string name;
                try
                {
                    name = WaveInEvent.GetCapabilities(i).ProductName;
                }
                catch (Exception ex)
                {
                    name = $"Устройство {i} ({ex.GetType().Name})";
                }

                devices.Add(new MicDevice(i, name, true, string.Empty));
            }
        }
        catch
        {
            // WinMM is unavailable (audio service down, remote session, ...).
            // The WASAPI fallback below still gives the user something to act on.
        }

        if (devices.Count > 0)
        {
            return devices;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            // 0xF = Active | Disabled | NotPresent | Unplugged. NAudio exposes
            // this as DeviceState.All; the numeric mask keeps this working on
            // every 2.x build.
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, (DeviceState)0xF))
            {
                // NotPresent entries are driver placeholders (rear/front jacks
                // that were never fitted). Showing a dozen of them is noise.
                if (device.State == DeviceState.NotPresent)
                {
                    continue;
                }

                var note = device.State switch
                {
                    DeviceState.Disabled => "отключено в Windows",
                    DeviceState.Unplugged => "кабель не подключён",
                    DeviceState.Active => "активно, но недоступно для записи",
                    _ => device.State.ToString()
                };

                devices.Add(new MicDevice(-1, device.FriendlyName, false, note));
            }
        }
        catch
        {
            // No enumerator (e.g. no audio service at all): the wizard shows the
            // explicit empty-state instruction instead of an empty ComboBox.
        }

        return devices;
    }
}
