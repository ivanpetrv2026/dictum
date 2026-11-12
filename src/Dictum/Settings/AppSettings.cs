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

    public string HotkeyPreset { get; set; } = "ctrl+win";

    public int AudioDevice { get; set; }

    public int MinRecordMs { get; set; } = 300;

    public bool LearnEnabled { get; set; } = true;

    public bool SaveTranscripts { get; set; } = true;

    public bool AiEnabled { get; set; }

    public bool AiFallbackEnabled { get; set; } = true;

    public string AiFallbackModel { get; set; } = "gemini-3-flash-preview";

    public string AiModel { get; set; } = "anthropic/claude-haiku-4.5";

    public string AiProvider { get; set; } = "openrouter";

    public string AiBaseUrl { get; set; } = "https://openrouter.ai/api/v1/chat/completions";

    public int AiMinWords { get; set; } = 6;

    public int AiTimeoutSeconds { get; set; } = 8;
}
