using System.Runtime.InteropServices;

namespace Dictum.Input;

/// <summary>
/// A push-to-talk chord: any combination of modifiers plus an optional key.
/// Polled with GetAsyncKeyState — no keyboard hook, so the app keeps the clean
/// profile that antivirus heuristics expect.
/// </summary>
public sealed class HotkeyDefinition
{
    public bool Ctrl { get; init; }

    public bool Alt { get; init; }

    public bool Shift { get; init; }

    public bool Win { get; init; }

    public int VirtualKey { get; init; }

    public static readonly (string Id, string Label)[] Presets =
    {
        ("ctrl+win", "Ctrl + Win"),
        ("ctrl+alt", "Ctrl + Alt"),
        ("alt+space", "Alt + Space"),
        ("ctrl+shift+space", "Ctrl + Shift + Space"),
        ("f8", "F8")
    };

    public static HotkeyDefinition Parse(string? preset) => preset switch
    {
        "ctrl+alt" => new HotkeyDefinition { Ctrl = true, Alt = true },
        "alt+space" => new HotkeyDefinition { Alt = true, VirtualKey = 0x20 },
        "ctrl+shift+space" => new HotkeyDefinition { Ctrl = true, Shift = true, VirtualKey = 0x20 },
        "f8" => new HotkeyDefinition { VirtualKey = 0x77 },
        _ => new HotkeyDefinition { Ctrl = true, Win = true }
    };

    public static string Label(string? preset) =>
        Presets.FirstOrDefault(item => item.Id == preset).Label ?? "Ctrl + Win";

    public bool IsDown()
    {
        if (Ctrl && !NativeKeyState.IsDown(0x11)) return false;
        if (Alt && !NativeKeyState.IsDown(0x12)) return false;
        if (Shift && !NativeKeyState.IsDown(0x10)) return false;
        if (Win && !NativeKeyState.IsDown(0x5B)) return false;
        if (VirtualKey != 0 && !NativeKeyState.IsDown(VirtualKey)) return false;

        return Ctrl || Alt || Shift || Win || VirtualKey != 0;
    }
}

internal static class NativeKeyState
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
