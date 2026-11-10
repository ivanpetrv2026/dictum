using System.Runtime.InteropServices;

namespace Dictum.Input;

/// <summary>
/// Types text into the focused window with SendInput + KEYEVENTF_UNICODE.
/// No clipboard writes: clipboard access is heavily weighted by AV heuristics
/// and breaks in RDP sessions where clipboard redirection is disabled.
/// </summary>
public sealed class TextInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    public nint CaptureTarget() => GetForegroundWindow();

    public void Focus(nint window)
    {
        if (window != nint.Zero && window != GetForegroundWindow())
        {
            SetForegroundWindow(window);
        }
    }

    public void TypeText(string text)
    {
        foreach (var ch in text)
        {
            SendUnicode(ch);
        }
    }

    private static void SendUnicode(char ch)
    {
        var inputs = new INPUT[2];

        inputs[0].type = InputKeyboard;
        inputs[0].u.ki.wScan = ch;
        inputs[0].u.ki.dwFlags = KeyEventUnicode;

        inputs[1].type = InputKeyboard;
        inputs[1].u.ki.wScan = ch;
        inputs[1].u.ki.dwFlags = KeyEventUnicode | KeyEventKeyUp;

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            // Synthetic input was blocked (for example, an elevated window has focus).
            return;
        }

        Thread.Sleep(1);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
