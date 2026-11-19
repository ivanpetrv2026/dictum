using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dictum.Settings;

public sealed class SettingsLoadResult
{
    public AppSettings Settings { get; init; } = new();

    /// <summary>The parsed file, including keys this build does not know about.</summary>
    public JsonObject? Original { get; init; }

    /// <summary>Raw bytes when the file could not be parsed at all.</summary>
    public byte[]? UnreadableBytes { get; init; }

    public IReadOnlyList<string> RepairedFields { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Pure, testable settings persistence:
/// - a bad field falls back to its default and is named, not the whole file;
/// - unknown keys survive a save (forward compatibility);
/// - an unreadable file is preserved under a content-addressed name;
/// - writes go through a temp file in the same directory, so a crash cannot
///   leave a half-written settings.json.
/// </summary>
public static class SettingsGuard
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static SettingsLoadResult Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SettingsLoadResult();
        }

        var bytes = File.ReadAllBytes(path);

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(bytes) as JsonObject;
            if (root is null)
            {
                return new SettingsLoadResult { UnreadableBytes = bytes };
            }
        }
        catch (JsonException)
        {
            return new SettingsLoadResult { UnreadableBytes = bytes };
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(root.ToJsonString(), Options)
                           ?? new AppSettings();
            return new SettingsLoadResult { Settings = settings, Original = root };
        }
        catch (JsonException)
        {
            return Salvage(root);
        }
    }

    private static SettingsLoadResult Salvage(JsonObject root)
    {
        var settings = new AppSettings();
        var repaired = new List<string>();

        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || !root.TryGetPropertyValue(property.Name, out var node) || node is null)
            {
                continue;
            }

            try
            {
                var value = JsonSerializer.Deserialize(node.ToJsonString(), property.PropertyType, Options);
                property.SetValue(settings, value);
            }
            catch (JsonException)
            {
                repaired.Add(property.Name);
            }
            catch (NotSupportedException)
            {
                repaired.Add(property.Name);
            }
            catch (ArgumentException)
            {
                repaired.Add(property.Name);
            }
        }

        return new SettingsLoadResult
        {
            Settings = settings,
            Original = root,
            RepairedFields = repaired
        };
    }

    public static string BackupNameFor(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var shortHash = Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        return $"settings.json.unreadable-{shortHash}";
    }

    public static void Save(
        string path,
        AppSettings settings,
        JsonObject? original,
        byte[]? unreadableBytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (unreadableBytes is not null)
        {
            var backup = Path.Combine(directory ?? ".", BackupNameFor(unreadableBytes));
            if (!File.Exists(backup))
            {
                File.WriteAllBytes(backup, unreadableBytes);
            }
        }
        else if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", true);
        }

        var merged = original?.DeepClone() as JsonObject ?? new JsonObject();
        if (JsonSerializer.SerializeToNode(settings, Options) is JsonObject known)
        {
            foreach (var (key, value) in known)
            {
                merged[key] = value?.DeepClone();
            }
        }

        var json = merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
