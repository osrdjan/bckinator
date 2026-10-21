using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bckinator.Core;

public sealed record VersionedDocument<T>(int SchemaVersion, T Data);

public sealed class ProfileStore(string? root = null)
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public string Root { get; } = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bckinator");
    public string RunsDirectory => Path.Combine(Root, "runs");
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<T?> ReadAsync<T>(string filename, CancellationToken ct = default)
    {
        string path = Path.Combine(Root, filename);
        if (!File.Exists(path)) return default;
        using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<VersionedDocument<T>>(stream, JsonOptions, ct)
            ?? throw new InvalidDataException($"{filename} is empty or invalid.");
        if (document.SchemaVersion != 1) throw new InvalidDataException($"Unsupported schema version {document.SchemaVersion} in {filename}. Expected version 1.");
        return document.Data ?? throw new InvalidDataException($"{filename} has no data.");
    }

    public async Task WriteAsync<T>(string filename, T data, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        string temporary = "";
        try
        {
            Directory.CreateDirectory(Root);
            string path = Path.Combine(Root, filename);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, new VersionedDocument<T>(1, data), JsonOptions, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (temporary.Length > 0 && File.Exists(temporary)) File.Delete(temporary);
            gate.Release();
        }
    }

    public static BackupProfile Clone(BackupProfile profile) =>
        JsonSerializer.Deserialize<BackupProfile>(JsonSerializer.Serialize(profile, JsonOptions), JsonOptions)!;

    public static string Export(BackupProfile profile) => JsonSerializer.Serialize(new VersionedDocument<BackupProfile>(1, profile), JsonOptions);

    public static BackupProfile Import(string json)
    {
        var document = JsonSerializer.Deserialize<VersionedDocument<BackupProfile>>(json, JsonOptions)
            ?? throw new InvalidDataException("The profile document is invalid.");
        if (document.SchemaVersion != 1) throw new InvalidDataException("This profile schema version is not supported.");
        var profile = document.Data ?? throw new InvalidDataException("The profile document has no profile data.");
        ProfileValidator.ValidateDefinition(profile);
        profile.Id = Guid.NewGuid();
        return profile;
    }

    public static BackupProfile CreateSample()
    {
        using var stream = typeof(ProfileStore).Assembly.GetManifestResourceStream("Bckinator.Core.Samples.Woc.json")
            ?? throw new InvalidDataException("The bundled sample profile is missing.");
        using var reader = new StreamReader(stream);
        return Import(reader.ReadToEnd());
    }
}
