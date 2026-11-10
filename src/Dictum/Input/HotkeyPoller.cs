using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Dictum.Input;

/// <summary>
/// Detects a chord of modifier keys by polling key state instead of installing
/// a low-level keyboard hook. WH_KEYBOARD_LL is what keyloggers use, and it is
/// the single biggest reason dictation utilities get flagged by antivirus.
/// Polling two keys every 30 ms costs nothing and keeps the process clean.
/// </summary>
public sealed class HotkeyPoller : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _isDown;

    public event EventHandler? Pressed;

    public event EventHandler? Released;

    public HotkeyPoller()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var down = NativeKeyState.IsDown(NativeKeyState.VkControl)
                   && NativeKeyState.IsDown(NativeKeyState.VkLeftWin);

        if (down == _isDown)
        {
            return;
        }

        _isDown = down;

        if (down)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Released?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}

internal static class NativeKeyState
{
    public const int VkControl = 0x11;
    public const int VkLeftWin = 0x5B;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
