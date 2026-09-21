using System.Text;
using Microsoft.Extensions.AI;

namespace AgentMemory.Inference;

/// <summary>
/// Prints every request and reply, with the key and the endpoint's path scrubbed.
/// </summary>
/// <remarks>
/// <para>
/// Wired by <see cref="InferenceClientFactory"/> when <c>AGENTMEMORY_INFERENCE_SHOW_RAW</c> is set.
/// It exists because the alternative to reading the actual bytes is guessing why a host rejected a
/// prompt, and this repository has spent real money on that guess.
/// </para>
/// <para>
/// <b>Writes to stderr by default, and that is not a detail.</b> The MCP host speaks JSON-RPC over
/// stdout; a diagnostic line there corrupts the protocol stream rather than merely cluttering it.
/// </para>
/// <para>
/// <b>One lock, one write per call.</b> Concurrent turns interleaving mid-line produce a transcript
/// that cannot be read, which is the same as no transcript.
/// </para>
/// </remarks>
internal sealed class RawInferenceLoggingChatClient(
    IChatClient inner, string purpose, string safeEndpoint, TextWriter writer)
    : DelegatingChatClient(inner)
{
    // A plain object, not System.Threading.Lock: this package multi-targets net8.0, where that
    // type does not exist. Static, so an answer client and a judge client cannot interleave.
    private static readonly object Gate = new();

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var materialised = messages as IList<ChatMessage> ?? [.. messages];
        Write("request", materialised.Select(m => $"{m.Role}: {m.Text}"));

        var response = await base.GetResponseAsync(materialised, options, cancellationToken)
            .ConfigureAwait(false);

        Write("reply", response.Messages.Select(m => $"{m.Role}: {m.Text}"));
        return response;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The streaming path logs the ASSEMBLED reply rather than each update: a token-by-token
    /// transcript is unreadable, and the updates are forwarded untouched as they arrive so nothing
    /// about the caller's streaming behaviour changes.
    /// </remarks>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var materialised = messages as IList<ChatMessage> ?? [.. messages];
        Write("request (streaming)", materialised.Select(m => $"{m.Role}: {m.Text}"));

        var assembled = new StringBuilder();
        await foreach (var update in base
            .GetStreamingResponseAsync(materialised, options, cancellationToken)
            .ConfigureAwait(false))
        {
            assembled.Append(update.Text);
            yield return update;
        }

        Write("reply (streaming)", [assembled.ToString()]);
    }

    private void Write(string label, IEnumerable<string> lines)
    {
        var text = new StringBuilder()
            .Append("─── ").Append(purpose).Append(' ').Append(label)
            .Append(" @ ").Append(safeEndpoint).AppendLine(" ───");

        foreach (var line in lines) text.AppendLine(line);

        lock (Gate)
        {
            writer.Write(text.ToString());
            // Unbuffered: a transcript that arrives after the crash it was meant to explain is no use.
            writer.Flush();
        }
    }
}
