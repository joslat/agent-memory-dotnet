// One source compiled into the format verifier (PackErasure) and the module stores (ModuleErasure), as RecordFrame.cs is shared:
// the hash a notice names is the hash a store keeps as a tombstone (feature 08), with no copy of the rule.
using System.Security.Cryptography;
using System.Text;

#if AGENTMEMORY_PACKS_FORMAT
namespace AgentMemory.Packs.Format;
#else
namespace AgentMemory.Extensibility;
#endif

/// <summary>The salted hash of an erased record's key (R-CORE-131): SHA-256 of the salt, a colon and the key in UTF-8, lowercase hex.</summary>
internal static class ErasureHash
{
    internal static string Of(string salt, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + key))).ToLowerInvariant();
}
