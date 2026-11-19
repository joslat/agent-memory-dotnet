// SPDX-License-Identifier: Apache-2.0
namespace AgentMemory.Packs.Format;

/// <summary>
/// The reader-only build has no signer.
/// <para>Three files of the format library (certificates, aggregates, erasures) hold one function that takes a signer beside the
/// functions that verify. They are compiled here unchanged, because recorded sabotage checks of the repository are pinned to the
/// exact contents of those files (eng/guards). This type lets them compile and makes them impossible to use: it is sealed, its
/// only constructor is private and nothing here calls it, so no signer can exist in this assembly; it holds no key and calls no
/// signing function of .NET. The real signer (PackSigner.cs of the format library) is not compiled into this assembly.</para>
/// </summary>
internal sealed class PackSigner
{
    private const string No = "This client verifies and reads. It does not sign.";

    private PackSigner() { }

    internal string KeyId => throw new NotSupportedException(No);

    internal byte[] Sign(byte[] data) => throw new NotSupportedException(No);
}
