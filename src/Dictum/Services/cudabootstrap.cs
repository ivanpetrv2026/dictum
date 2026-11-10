using System.IO;
using System.Runtime.InteropServices;

namespace Dictum.Services;

/// <summary>
/// whisper.cpp's CUDA backend links against cuBLAS and the CUDA runtime at
/// load time. Those DLLs are not redistributable through NuGet, so the app
/// looks for them in two places (in order):
///   1. a "cuda" folder next to the executable;
///   2. %LOCALAPPDATA%\Dictum\cuda — populated by tools/Get-CudaRuntime.ps1.
///
/// If neither exists, Whisper.net falls back to the CPU runtime: dictation
/// keeps working, just slower.
/// </summary>
internal static class CudaBootstrap
{
    private const int LoadLibrarySearchDefaultDirs = 0x00001000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(int directoryFlags);

    public static bool IsAvailable { get; private set; }

    public static string? Prepare()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "cuda"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Dictum",
                "cuda")
        };

        foreach (var directory in candidates)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var cublas = Path.Combine(directory, "cublas64_12.dll");
            var cudart = Path.Combine(directory, "cudart64_12.dll");

            if (!File.Exists(cublas))
            {
                Log.Write($"cuda: '{directory}' has no cublas64_12.dll, skipped");
                continue;
            }

            try
            {
                SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs);
                AddDllDirectory(directory);
                SetDllDirectory(directory);

                IsAvailable = true;
                Log.Write($"cuda: using '{directory}', cudart={File.Exists(cudart)}");
                return directory;
            }
            catch (Exception ex)
            {
                Log.Write("cuda: bootstrap failed: " + ex.Message);
            }
        }

        Log.Write("cuda: no runtime folder, using CPU");
        return null;
    }
}
