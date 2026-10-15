using System.Security.Cryptography;
using System.Text.Json;

namespace AgentMemory.McpHost.Coding.Client;

internal sealed class CodingHookState(CodingHookOptions options, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private const int Capacity = 128;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public async Task CaptureAsync(CodingRememberRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        EnsureDirectory();
        await using var admission = Open(Path.Combine(options.StateDirectory, "admission.lock"), FileMode.OpenOrCreate);
        var path = FilePath(request.Session, request.TurnId);
        if (!File.Exists(path)) await PruneAsync(cancellationToken).ConfigureAwait(false);
        await using var file = Open(path, FileMode.OpenOrCreate);
        if (file.Length != 0)
        {
            var existing = await ReadAsync(file, cancellationToken).ConfigureAwait(false);
            if (existing.Request is null || existing.Request.PayloadHash() == request.PayloadHash()) return;
            await WriteAsync(file, existing with { Request = null }, cancellationToken).ConfigureAwait(false);
            throw new ArgumentException("Conflicting hook capture was discarded.");
        }
        await WriteAsync(file, new Pending(request, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ConsumeAsync(string session, string prompt,
        Func<CodingRememberRequest, CancellationToken, Task> submit, CancellationToken cancellationToken = default)
    {
        EnsureDirectory();
        var path = FilePath(session, prompt);
        if (!File.Exists(path)) return false;
        await using var file = Open(path, FileMode.Open);
        var pending = await ReadAsync(file, cancellationToken).ConfigureAwait(false);
        if (pending.Request is null) return false;
        if (_time.GetUtcNow() - pending.CapturedAtUtc > Lifetime)
        {
            await WriteAsync(file, pending with { Request = null }, cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (pending.Request.Session != session || pending.Request.TurnId != prompt) throw new ArgumentException("Hook state does not match this turn.");
        await submit(pending.Request, cancellationToken).ConfigureAwait(false);
        // Persist a text-free tombstone while still holding the exclusive handle. A second process
        // cannot slip between releasing the lock and deleting the pending data and submit it again.
        await WriteAsync(file, pending with { Request = null }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public void Discard(string session, string prompt)
    {
        EnsureDirectory();
        var path = FilePath(session, prompt);
        if (!File.Exists(path)) return;
        using var file = Open(path, FileMode.Open);
        file.SetLength(0);
        JsonSerializer.Serialize(file, new Pending(null, _time.GetUtcNow()), CodingApi.Json);
        file.Flush();
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(options.StateDirectory, "turn-*.json").Where(IsOwnedName).Take(Capacity + 1).ToList();
        foreach (var path in files.ToArray())
        {
            try
            {
                RejectLinks(path);
                var expired = _time.GetUtcNow() - File.GetLastWriteTimeUtc(path) > Lifetime;
                if (!expired && files.Count < Capacity) continue;
                bool removable;
                await using (var file = Open(path, FileMode.Open))
                    removable = expired || (await ReadAsync(file, ct).ConfigureAwait(false)).Request is null;
                if (!removable) continue;
                File.Delete(path);
                files.Remove(path);
            }
            catch (IOException) { /* A live hook owns this entry; do not evict it. */ }
        }
        if (files.Count >= Capacity) throw new IOException("Hook pending-turn capacity reached.");
    }

    private void EnsureDirectory()
    {
        options.Validate();
        RejectLinks(options.StateDirectory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(options.StateDirectory);
        else Directory.CreateDirectory(options.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void RejectLinks(string path)
    {
        for (var item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Hook paths must not traverse links.");
    }

    private string FilePath(string session, string prompt)
    {
        CodingInput.RequireText(session, 256, nameof(session));
        CodingInput.RequireText(prompt, 256, nameof(prompt));
        string[] identity = [options.ServiceUrl, options.Project, Path.GetFullPath(options.RepositoryRoot), session, prompt];
        // Keep existing Claude cache filenames readable across upgrades; additional harnesses get distinct state.
        if (options.Harness != CodingHookHarness.Claude) identity = [.. identity, options.Harness.ToString()];
        var key = JsonSerializer.SerializeToUtf8Bytes(identity);
        return Path.Combine(options.StateDirectory, "turn-" + Convert.ToHexStringLower(SHA256.HashData(key)) + ".json");
    }

    private static bool IsOwnedName(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length == 74 && name[5..69].All(Uri.IsHexDigit);
    }

    private static FileStream Open(string path, FileMode mode)
    {
        RejectLinks(path);
        var settings = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows() && mode == FileMode.OpenOrCreate)
            settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, settings);
    }

    private static async Task<Pending> ReadAsync(FileStream file, CancellationToken ct)
    {
        if (file.Length > CodingBodyLimit.MaximumBytes) throw new ArgumentException("Oversized hook state.");
        return await JsonSerializer.DeserializeAsync<Pending>(file, CodingApi.Json, ct).ConfigureAwait(false)
            ?? throw new ArgumentException("Invalid hook state.");
    }

    private static async Task WriteAsync(FileStream file, Pending pending, CancellationToken ct)
    {
        file.Position = 0;
        file.SetLength(0);
        await JsonSerializer.SerializeAsync(file, pending, CodingApi.Json, ct).ConfigureAwait(false);
        await file.FlushAsync(ct).ConfigureAwait(false);
    }

    private sealed record Pending(CodingRememberRequest? Request, DateTimeOffset CapturedAtUtc);
}
