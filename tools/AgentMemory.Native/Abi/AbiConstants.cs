namespace AgentMemory.Native.Abi;

/// <summary>The C ABI's version, as native/include/agentmemory_native.h declares it.</summary>
internal static class AbiVersion
{
    public const uint Major = 1;
    public const uint Minor = 1;
    public const uint Packed = (Major << 16) | Minor;
}

/// <summary>Return values of every int32 export: the boundary call only, never an operation's outcome.</summary>
internal static class AbiStatus
{
    public const int Ok = 0;
    public const int InvalidArgument = 1;
    public const int UnsupportedAbi = 2;
    public const int InvalidHandle = 3;
    public const int Overloaded = 4;
    public const int Closing = 5;
    public const int InternalError = 6;
    public const int Busy = 7;
}

/// <summary>Whether a poll delivered a result.</summary>
internal static class PollDisposition
{
    public const uint Pending = 0;
    public const uint Terminal = 1;
}

/// <summary>Flags of the state snapshot.</summary>
internal static class StateFlags
{
    public const uint ReadAllowed = 1;
    public const uint AdmissionAllowed = 2;
    public const uint WriteQuiescent = 4;
    public const uint Closing = 8;
    public const uint ErasePending = 16;
}
