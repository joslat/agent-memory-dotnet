namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Facade over all memory operations, composed from the focused role interfaces
/// <see cref="IMemoryRecall"/> (read), <see cref="IMemoryIngestion"/> (write), and
/// <see cref="IMemoryMaintenance"/> (upkeep). Existing consumers can keep depending on
/// <see cref="IMemoryService"/>; new code that needs only one concern should depend on the
/// corresponding role interface to honor the Interface Segregation Principle. The default DI
/// registration binds all three roles to the same underlying implementation instance.
/// </summary>
public interface IMemoryService : IMemoryRecall, IMemoryIngestion, IMemoryMaintenance
{
}
