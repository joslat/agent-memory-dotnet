using System.Text.RegularExpressions;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Infrastructure;

public sealed class ClosingApplicationBoundaryTests
{
    [Fact]
    public void Ingestion_sources_delegate_model_judging_to_the_closing_policy()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentMemory.slnx"))) root = root.Parent;
        root.Should().NotBeNull();
        var extraction = Path.Combine(root!.FullName, "src", "AgentMemory.Core", "Extraction");
        var judgeCall = new Regex(@"\.JudgeAsync\s*\(");
        var bypasses = Directory.EnumerateFiles(extraction, "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "ClosingPolicy.cs")
            .Where(path => judgeCall.IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName).ToArray();
        bypasses.Should().BeEmpty("closing sources discover candidates; the policy owns model selection and approval");
    }

    [Fact]
    public void Automatic_ingestion_closings_use_the_common_application_boundary()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentMemory.slnx"))) root = root.Parent;
        root.Should().NotBeNull();
        var extraction = Path.Combine(root!.FullName, "src", "AgentMemory.Core", "Extraction");
        var closingCall = new Regex(@"\.(SupersedeAsync|EndAsync|InvalidateAsync)\s*\(");
        var bypasses = Directory.EnumerateFiles(extraction, "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "ClosingPipeline.cs")
            .Where(path => closingCall.IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName).ToArray();
        bypasses.Should().BeEmpty("every automatic ingestion closing must use typed common application; explicit maintenance APIs are outside this boundary");
    }
}
