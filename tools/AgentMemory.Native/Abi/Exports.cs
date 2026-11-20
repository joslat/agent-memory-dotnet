using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgentMemory.Native.Contract;
using AgentMemory.Native.Runtime;

namespace AgentMemory.Native.Abi;

/// <summary>
/// The C ABI of native/include/agentmemory_native.h. Each export validates its pointers, catches every managed
/// exception at the boundary and returns a status; nothing managed crosses it. An invalid non-null pointer cannot be
/// detected and is the caller's error.
/// </summary>
internal static unsafe class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "am_v1_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static uint AbiVersionExport() => AbiVersion.Packed;

    [UnmanagedCallersOnly(EntryPoint = "am_v1_open", CallConvs = [typeof(CallConvCdecl)])]
    public static int Open(uint requestedAbi, byte* config, nuint configLength, ulong* outContext, byte** outError, nuint* outErrorLength)
    {
        if (outContext != null) *outContext = 0;
        if (outError != null) *outError = null;
        if (outErrorLength != null) *outErrorLength = 0;
        try
        {
            if (outContext == null || config == null || configLength == 0)
                return Fail(AbiStatus.InvalidArgument, "invalid_argument", "config and out_context are required.", outError, outErrorLength);
            if (configLength > StrictObject.MaxDocumentBytes)
                return Fail(AbiStatus.InvalidArgument, "too_large",
                    $"The configuration is over {StrictObject.MaxDocumentBytes} bytes.", outError, outErrorLength);

            var (status, handle, error) = ContextRegistry.Open(requestedAbi, Copy(config, configLength));
            *outContext = handle;
            if (error is not null) WriteBuffer(error, outError, outErrorLength);
            return status;
        }
        catch (Exception exception)
        {
            return Fail(AbiStatus.InternalError, "internal", $"{exception.GetType().Name}: {exception.Message}", outError, outErrorLength);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "am_v1_submit", CallConvs = [typeof(CallConvCdecl)])]
    public static int Submit(ulong context, byte* request, nuint requestLength, uint remainingTimeoutMs, ulong* outJob)
    {
        if (outJob != null) *outJob = 0;
        try
        {
            if (outJob == null || request == null || requestLength == 0 || requestLength > StrictObject.MaxDocumentBytes)
                return AbiStatus.InvalidArgument;
            if (!ContextRegistry.TryGet(context, out var target)) return AbiStatus.InvalidHandle;
            var status = target.Submit(Copy(request, requestLength), remainingTimeoutMs, out var job);
            *outJob = job;
            return status;
        }
        catch (Exception)
        {
            return AbiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "am_v1_poll", CallConvs = [typeof(CallConvCdecl)])]
    public static int Poll(ulong context, ulong job, uint waitMs, uint* outDisposition, byte** outResult, nuint* outResultLength)
    {
        if (outDisposition != null) *outDisposition = PollDisposition.Pending;
        if (outResult != null) *outResult = null;
        if (outResultLength != null) *outResultLength = 0;
        try
        {
            if (outDisposition == null || outResult == null || outResultLength == null) return AbiStatus.InvalidArgument;
            if (!ContextRegistry.TryGet(context, out var target)) return AbiStatus.InvalidHandle;
            var status = target.Poll(job, waitMs, out var result);
            if (status == AbiStatus.Ok && result is not null)
            {
                WriteBuffer(result, outResult, outResultLength);
                *outDisposition = PollDisposition.Terminal;
            }
            return status;
        }
        catch (Exception)
        {
            return AbiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "am_v1_cancel", CallConvs = [typeof(CallConvCdecl)])]
    public static int Cancel(ulong context, ulong job)
    {
        try
        {
            return ContextRegistry.TryGet(context, out var target) ? target.Cancel(job) : AbiStatus.InvalidHandle;
        }
        catch (Exception)
        {
            return AbiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "am_v1_job_release", CallConvs = [typeof(CallConvCdecl)])]
    public static int JobRelease(ulong context, ulong job)
    {
        try
        {
            return ContextRegistry.TryGet(context, out var target) ? target.Release(job) : AbiStatus.InvalidHandle;
        }
        catch (Exception)
        {
            return AbiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "am_v1_buffer_free", CallConvs = [typeof(CallConvCdecl)])]
    public static void BufferFree(void* buffer)
    {
        if (buffer != null) NativeMemory.Free(buffer);
    }

    [UnmanagedCallersOnly(EntryPoint = "am_v1_state", CallConvs = [typeof(CallConvCdecl)])]
    public static int State(ulong context, ulong* outStateRevision, ulong* outPolicyEpoch, ulong* outDeletionEpoch,
        ulong* outMemoryRevision, uint* outFlags)
    {
        if (outStateRevision != null) *outStateRevision = 0;
        if (outPolicyEpoch != null) *outPolicyEpoch = 0;
        if (outDeletionEpoch != null) *outDeletionEpoch = 0;
        if (outMemoryRevision != null) *outMemoryRevision = 0;
        if (outFlags != null) *outFlags = 0;
        try
        {
            if (outStateRevision == null || outPolicyEpoch == null || outDeletionEpoch == null || outMemoryRevision == null
                || outFlags == null)
                return AbiStatus.InvalidArgument;
            if (!ContextRegistry.TryGet(context, out var target)) return AbiStatus.InvalidHandle;
            target.State(out var revision, out var flags);
            *outStateRevision = revision;
            *outFlags = flags;
            return AbiStatus.Ok;
        }
        catch (Exception)
        {
            return AbiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "am_v1_close", CallConvs = [typeof(CallConvCdecl)])]
    public static int Close(ulong context)
    {
        try
        {
            return ContextRegistry.Close(context);
        }
        catch (Exception)
        {
            return AbiStatus.InternalError;
        }
    }

    private static byte[] Copy(byte* source, nuint length) => new ReadOnlySpan<byte>(source, checked((int)length)).ToArray();

    /// <summary>A buffer the caller owns and frees with <c>am_v1_buffer_free</c>; never zero bytes long.</summary>
    private static void WriteBuffer(byte[] bytes, byte** outBuffer, nuint* outLength)
    {
        if (outBuffer == null || outLength == null) return;
        var buffer = (byte*)NativeMemory.Alloc((nuint)Math.Max(bytes.Length, 1));
        bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
        *outBuffer = buffer;
        *outLength = (nuint)bytes.Length;
    }

    private static int Fail(int status, string code, string message, byte** outError, nuint* outErrorLength)
    {
        try
        {
            WriteBuffer(ResultWriter.OpenError(code, message), outError, outErrorLength);
        }
        catch (Exception)
        {
            // Out of memory while reporting a failure: the status alone must do.
        }
        return status;
    }
}
