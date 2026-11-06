// SPDX-License-Identifier: Apache-2.0
namespace AgentMemory.Packs.Format;

/// <summary>The host ledger stayed locked by another run: an operational failure, not a refusal.</summary>
internal sealed class LedgerBusyException(string message, Exception inner) : IOException(message, inner);
