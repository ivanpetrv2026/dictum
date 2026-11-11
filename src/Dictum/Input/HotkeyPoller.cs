using System.Windows.Threading;

namespace Dictum.Input;

/// <summary>
/// Detects the configured chord by polling key state instead of installing a
/// low-level keyboard hook. WH_KEYBOARD_LL is what keyloggers use and the main
/// reason dictation utilities get flagged; polling a few keys every 30 ms costs
/// nothing and keeps the process clean.
/// </summary>
public sealed class HotkeyPoller : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly HotkeyDefinition _definition;
    private bool _isDown;

    public event EventHandler? Pressed;

    public event EventHandler? Released;

    public HotkeyPoller(HotkeyDefinition definition)
    {
        _definition = definition;

        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var down = _definition.IsDown();

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
