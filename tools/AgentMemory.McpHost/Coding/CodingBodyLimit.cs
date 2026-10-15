namespace AgentMemory.McpHost.Coding;

/// <summary>Bounds HTTP and MCP bodies before parsing, including requests without Content-Length.</summary>
internal sealed class CodingBodyLimit(RequestDelegate next)
{
    internal const int MaximumBytes = 64 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaximumBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        var original = context.Request.Body;
        var bytes = new byte[MaximumBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await original.ReadAsync(bytes.AsMemory(length), context.RequestAborted).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > MaximumBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        using var buffered = new MemoryStream(bytes, 0, length, writable: false);
        context.Request.Body = buffered;
        try { await next(context).ConfigureAwait(false); }
        finally { context.Request.Body = original; }
    }
}
