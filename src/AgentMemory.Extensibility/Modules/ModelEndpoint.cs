// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// Where the host's model runs, as its operator states it (feature 10, R-CORE-139): a pack's usage policy allows its records'
/// text to reach a model of one class (<c>rehydrate:local</c>, <c>rehydrate:private</c>, <c>rehydrate:external</c>) and not
/// another. A host that states none is external, so a pack that keeps its text from outside models is withheld.
/// </summary>
[Experimental("AMEXT001")]
public enum ModelEndpoint
{
    /// <summary>A third party's model endpoint, or one the operator has not classed.</summary>
    External,
    /// <summary>A model in the operator's own private cloud.</summary>
    Private,
    /// <summary>A model running on the host itself.</summary>
    Local,
}
