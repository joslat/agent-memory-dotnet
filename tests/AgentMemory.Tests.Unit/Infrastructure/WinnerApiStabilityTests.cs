using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Extraction.Llm;
using AgentMemory.Gate;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Infrastructure;

public sealed class WinnerApiStabilityTests
{
    [Fact]
    public void Recommended_and_writer_public_contracts_are_stable()
    {
        var assemblies = new[] { typeof(MemoryOptions).Assembly, typeof(LlmExtractionOptions).Assembly,
            typeof(AgentFrameworkOptions).Assembly };
        var experimental = assemblies.SelectMany(PublicMembers)
            .Where(m => m.GetCustomAttribute<ExperimentalAttribute>()?.DiagnosticId is "AMWRITE001" or "AMREC001")
            .Select(m => $"{m.DeclaringType?.FullName ?? m.Name}.{m.Name}").ToArray();
        experimental.Should().BeEmpty("the selected writer and Recommended composition are supported 2.0 APIs");
    }

    [Fact]
    public void Selected_gate_and_update_judge_contracts_are_stable()
    {
        Type[] contracts = [typeof(IMemoryGate), typeof(MemoryGateCandidate), typeof(MemoryGateTurn),
            typeof(MemoryGateRequest), typeof(MemoryGateDecision), typeof(IMemoryUpdateJudge),
            typeof(MemoryUpdateRequest), typeof(MemoryUpdatePair), typeof(MemoryGateMode),
            typeof(SystemOneEndpoint), typeof(MemoryGateOptions), typeof(MemoryGateTrace),
            typeof(GatedMemoryContextAssembler), typeof(GateServiceCollectionExtensions),
            typeof(SystemOneMemoryGate), typeof(SystemOneUpdateJudge)];
        contracts.Where(t => t.GetCustomAttribute<ExperimentalAttribute>() is not null)
            .Should().BeEmpty("ordinary judging must not require experimental suppression");
        typeof(ExtractionOptions).GetProperty(nameof(ExtractionOptions.UpdateJudgeThreshold))!
            .GetCustomAttribute<ExperimentalAttribute>().Should().BeNull();
        typeof(ExtractionOptions).GetProperty(nameof(ExtractionOptions.UpdateJudgeCandidates))!
            .GetCustomAttribute<ExperimentalAttribute>().Should().BeNull();
        typeof(GatedMemoryContextAssembler).GetConstructors().Single(c => c.GetParameters().Length == 6)
            .GetCustomAttribute<ExperimentalAttribute>().Should().BeNull();
    }

    [Fact]
    public void Compound_planning_remains_explicitly_experimental()
    {
        MemberInfo[] members = [typeof(ICompoundQuestionPlanner), typeof(CompoundQuestionRequest),
            typeof(CompoundQuestionTrace), typeof(MemoryGateOptions).GetProperty(nameof(MemoryGateOptions.CompoundQuestions))!,
            typeof(MemoryGateOptions).GetProperty(nameof(MemoryGateOptions.CompoundDetector))!,
            typeof(GatedMemoryContextAssembler).GetConstructors().Single(c => c.GetParameters().Length == 7)];
        foreach (var member in members)
            (member.GetCustomAttribute<ExperimentalAttribute>()?.DiagnosticId).Should().Be("AMGATE001", member.Name);
    }

    private static IEnumerable<MemberInfo> PublicMembers(Assembly assembly) => assembly.GetExportedTypes()
        .SelectMany(t => new MemberInfo[] { t }.Concat(t.GetMembers(BindingFlags.Public | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly)));
}
