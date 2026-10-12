using System.Diagnostics;

namespace AgentMemory.McpHost.Coding.Client;

internal sealed class CodingRepository(string root, Func<string, string, CancellationToken, Task<string>>? run = null)
{
    public async Task<string> ReadCommitAsync(string cwd, CancellationToken cancellationToken = default)
    {
        var command = run ?? RunGitAsync;
        CodingHookState.RejectLinks(root);
        CodingHookState.RejectLinks(cwd);
        if (!Path.IsPathFullyQualified(cwd) || !CodingHookOptions.IsWithin(root, cwd))
            throw new ArgumentException("Hook is outside its configured repository.");
        var actual = await command(cwd, "--show-toplevel", cancellationToken).ConfigureAwait(false);
        if (!Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Hook belongs to a different or nested repository.");
        var hash = await command(cwd, "HEAD", cancellationToken).ConfigureAwait(false);
        if (hash.Length is not (40 or 64) || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("Hook repository has no valid commit.");
        return hash;
    }

    private static async Task<string> RunGitAsync(string cwd, string value, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-C", cwd, "rev-parse", value }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Git.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Cannot identify the hook repository.");
            var result = (await output.ConfigureAwait(false)).Trim();
            if (result.Length > 4096) throw new IOException("Unexpected Git response.");
            return result;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
