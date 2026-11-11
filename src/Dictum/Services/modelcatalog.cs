using System.IO;
using System.Net.Http;

namespace Dictum.Services;

public sealed record ModelInfo(string Title, string FileName, long SizeBytes)
{
    public string Url => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName;

    public string SizeText => $"{SizeBytes / 1048576.0:F0} МБ";
}

/// <summary>
/// Whisper models the app can download and manage. Files live in
/// %LOCALAPPDATA%\Dictum\models; nothing is bundled into the installer.
/// </summary>
public static class ModelCatalog
{
    public static readonly ModelInfo[] Models =
    {
        new("large-v3-turbo — лучшее качество", "ggml-large-v3-turbo.bin", 1_624_555_275),
        new("medium — хорошее качество", "ggml-medium.bin", 1_533_763_059),
        new("small — быстрее, меньше памяти", "ggml-small.bin", 487_601_967),
        new("base — самый лёгкий", "ggml-base.bin", 147_951_465)
    };

    public static string ModelsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dictum",
        "models");

    public static string PathFor(ModelInfo model) => Path.Combine(ModelsDirectory, model.FileName);

    public static bool IsInstalled(ModelInfo model)
    {
        var path = PathFor(model);
        return File.Exists(path) && new FileInfo(path).Length > 1_000_000;
    }

    public static async Task DownloadAsync(
        ModelInfo model,
        IProgress<(long Read, long Total)> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var destination = PathFor(model);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? model.SizeBytes;
        var buffer = new byte[81920];
        long read = 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);

        int chunk;
        while ((chunk = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, chunk), cancellationToken);
            read += chunk;
            progress.Report((read, total));
        }
    }
}
