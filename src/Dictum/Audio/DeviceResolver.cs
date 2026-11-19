namespace Dictum.Audio;

/// <summary>
/// Why a recording used the device it used. Keeping the reason is what turns a
/// silent "skipped" take into an actionable diagnostic: a saved microphone that
/// disappeared is a different problem from a microphone that hums.
/// </summary>
public enum DeviceResolutionReason
{
    SavedName,
    SavedIndex,
    SystemDefault,
    SavedNameMissing,
    NoDevices
}

public sealed record DeviceResolution(
    int Index,
    string Name,
    DeviceResolutionReason Reason,
    string Detail)
{
    public bool IsUsable => Reason != DeviceResolutionReason.NoDevices;

    public bool IsFallback => Reason is DeviceResolutionReason.SavedNameMissing
        or DeviceResolutionReason.SystemDefault;
}

/// <summary>
/// Pure input resolution over the WinMM device list. No Win32 calls here, so
/// the decision can be unit-tested without a microphone.
/// </summary>
public static class DeviceResolver
{
    public static DeviceResolution Resolve(
        IReadOnlyList<string> devices,
        string? savedName,
        int savedIndex)
    {
        if (devices.Count == 0)
        {
            return new DeviceResolution(
                -1,
                string.Empty,
                DeviceResolutionReason.NoDevices,
                "no capture devices are listed");
        }

        if (!string.IsNullOrWhiteSpace(savedName))
        {
            var wanted = savedName.Trim();
            for (var i = 0; i < devices.Count; i++)
            {
                if (string.Equals(devices[i], wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return new DeviceResolution(
                        i,
                        devices[i],
                        DeviceResolutionReason.SavedName,
                        $"saved microphone '{wanted}'");
                }
            }

            return new DeviceResolution(
                0,
                devices[0],
                DeviceResolutionReason.SavedNameMissing,
                $"saved microphone '{wanted}' is not listed; using the system default");
        }

        if (savedIndex >= 0 && savedIndex < devices.Count)
        {
            return new DeviceResolution(
                savedIndex,
                devices[savedIndex],
                DeviceResolutionReason.SavedIndex,
                $"legacy index {savedIndex}");
        }

        return new DeviceResolution(
            0,
            devices[0],
            DeviceResolutionReason.SystemDefault,
            $"index {savedIndex} is out of range; using the system default");
    }
}
