using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AgentMemory.Tests.Unit")]
[assembly: InternalsVisibleTo("AgentMemory.Tests.Integration")]

// The extensibility provider admits MODULE text into the same instruction block. It goes through
// MafTypeMapper.AdmitItem rather than carrying its own copy, for the reason that method's own remarks
// give: "a host that installs a custom admission policy must not find it applied everywhere except
// one place." A second copy would also be a second place for SecurityMode to be forgotten.
[assembly: InternalsVisibleTo("AgentMemory.Extensibility.AgentFramework")]
