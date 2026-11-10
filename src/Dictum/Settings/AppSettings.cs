namespace Dictum.Settings;

/// <summary>
/// User-facing configuration. Deliberately small: no autostart, no telemetry,
/// no background update channel — anything that looks like persistence is opt-in
/// and lives outside this file.
/// </summary>
public sealed class AppSettings
{
    public string ModelPath { get; set; } = string.Empty;

    public string Language { get; set; } = "ru";

    public int MinRecordMs { get; set; } = 300;
}
