using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dictum.Input;

public static class WindowInfo
{
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    /// <summary>Process name of the window the user is dictating into.</summary>
    public static string ProcessName(nint window)
    {
        try
        {
            var threadId = GetWindowThreadProcessId(window, out var processId);
            if (threadId == 0 || processId == 0)
            {
                return string.Empty;
            }

            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
