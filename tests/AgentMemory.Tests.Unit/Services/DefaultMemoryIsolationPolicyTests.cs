using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

public sealed class DefaultMemoryIsolationPolicyTests
{
    private static DefaultMemoryIsolationPolicy Create(MemoryIsolationMode mode, out ILogger<DefaultMemoryIsolationPolicy> logger)
    {
        logger = Substitute.For<ILogger<DefaultMemoryIsolationPolicy>>();
        var options = Options.Create(new MemoryIsolationOptions { Mode = mode });
        return new DefaultMemoryIsolationPolicy(options, logger);
    }

    private static bool LoggedWarning(ILogger<DefaultMemoryIsolationPolicy> logger) =>
        logger.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ILogger.Log)
            && c.GetArguments()[0] is LogLevel.Warning);

    // ── SingleTenant: today's exact behavior, never throws, never warns ──

    [Fact]
    public void SingleTenant_ResolveReadScope_NoOwner_ReturnsGlobal()
    {
        var policy = Create(MemoryIsolationMode.SingleTenant, out var logger);

        var scope = policy.ResolveReadScope(null, null, "Op", MemoryOperationAccess.Tenant);

        scope.HasOwnerFilter.Should().BeFalse();
        LoggedWarning(logger).Should().BeFalse();
    }

    [Fact]
    public void SingleTenant_ResolveReadScope_WithOwner_ScopesToOwner()
    {
        var policy = Create(MemoryIsolationMode.SingleTenant, out _);

        var scope = policy.ResolveReadScope(null, "alice", "Op", MemoryOperationAccess.Tenant);

        scope.OwnerId.Should().Be("alice");
    }

    [Fact]
    public void SingleTenant_ResolveReadScope_ExplicitScopeWinsOverOwnerId()
    {
        var policy = Create(MemoryIsolationMode.SingleTenant, out _);
        var explicitScope = MemoryScope.For("bob");

        var scope = policy.ResolveReadScope(explicitScope, "alice", "Op", MemoryOperationAccess.Tenant);

        scope.OwnerId.Should().Be("bob", "an explicit scope always wins over a bare ownerId");
    }

    [Fact]
    public void SingleTenant_ResolveWriteOwner_NoOwner_ReturnsNull()
    {
        var policy = Create(MemoryIsolationMode.SingleTenant, out _);

        policy.ResolveWriteOwner(null, "Op", MemoryOperationAccess.Tenant).Should().BeNull();
    }

    [Fact]
    public void SingleTenant_ResolveWriteOwner_WithOwner_ReturnsOwner()
    {
        var policy = Create(MemoryIsolationMode.SingleTenant, out _);

        policy.ResolveWriteOwner("alice", "Op", MemoryOperationAccess.Tenant).Should().Be("alice");
    }

    // ── WarnOnUnscoped: same resolution as SingleTenant, plus a structured warning ──

    [Fact]
    public void WarnOnUnscoped_UnscopedTenantRead_LogsWarning_ButStillReturnsGlobal()
    {
        var policy = Create(MemoryIsolationMode.WarnOnUnscoped, out var logger);

        var scope = policy.ResolveReadScope(null, null, "Op", MemoryOperationAccess.Tenant);

        scope.HasOwnerFilter.Should().BeFalse("WarnOnUnscoped preserves compatibility behavior");
        LoggedWarning(logger).Should().BeTrue();
    }

    [Fact]
    public void WarnOnUnscoped_UnscopedTenantWrite_LogsWarning_ButStillReturnsNull()
    {
        var policy = Create(MemoryIsolationMode.WarnOnUnscoped, out var logger);

        var owner = policy.ResolveWriteOwner(null, "Op", MemoryOperationAccess.Tenant);

        owner.Should().BeNull();
        LoggedWarning(logger).Should().BeTrue();
    }

    [Fact]
    public void WarnOnUnscoped_ScopedRead_DoesNotLog()
    {
        var policy = Create(MemoryIsolationMode.WarnOnUnscoped, out var logger);

        policy.ResolveReadScope(null, "alice", "Op", MemoryOperationAccess.Tenant);

        LoggedWarning(logger).Should().BeFalse();
    }

    [Fact]
    public void WarnOnUnscoped_AdministrativeAccess_NeverLogs()
    {
        var policy = Create(MemoryIsolationMode.WarnOnUnscoped, out var logger);

        policy.ResolveReadScope(null, null, "AdminOp", MemoryOperationAccess.Administrative);
        policy.ResolveWriteOwner(null, "AdminOp", MemoryOperationAccess.Administrative);

        LoggedWarning(logger).Should().BeFalse("administrative access is explicit, never accidental");
    }

    // ── StrictMultiTenant: fails closed for Tenant access, unaffected for Administrative ──

    [Fact]
    public void StrictMultiTenant_UnscopedTenantRead_Throws()
    {
        var policy = Create(MemoryIsolationMode.StrictMultiTenant, out _);

        var act = () => policy.ResolveReadScope(null, null, "RecallAsync", MemoryOperationAccess.Tenant);

        act.Should().Throw<MemoryOwnerScopeRequiredException>()
            .Which.OperationName.Should().Be("RecallAsync");
    }

    [Fact]
    public void StrictMultiTenant_UnscopedTenantWrite_Throws()
    {
        var policy = Create(MemoryIsolationMode.StrictMultiTenant, out _);

        var act = () => policy.ResolveWriteOwner(null, "ExtractAsync", MemoryOperationAccess.Tenant);

        act.Should().Throw<MemoryOwnerScopeRequiredException>()
            .Which.OperationName.Should().Be("ExtractAsync");
    }

    [Fact]
    public void StrictMultiTenant_ExplicitGlobalScope_StillThrowsForTenantAccess()
    {
        // An explicitly-passed MemoryScope.Global is still an unscoped result -- strict mode cares about
        // the EFFECTIVE resolved scope, not how the caller arrived at it.
        var policy = Create(MemoryIsolationMode.StrictMultiTenant, out _);

        var act = () => policy.ResolveReadScope(MemoryScope.Global, "alice-ignored-since-explicit-wins", "Op", MemoryOperationAccess.Tenant);

        act.Should().Throw<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public void StrictMultiTenant_ScopedTenantRead_Succeeds()
    {
        var policy = Create(MemoryIsolationMode.StrictMultiTenant, out _);

        var scope = policy.ResolveReadScope(null, "alice", "Op", MemoryOperationAccess.Tenant);

        scope.OwnerId.Should().Be("alice");
    }

    [Fact]
    public void StrictMultiTenant_ScopedTenantWrite_Succeeds()
    {
        var policy = Create(MemoryIsolationMode.StrictMultiTenant, out _);

        policy.ResolveWriteOwner("alice", "Op", MemoryOperationAccess.Tenant).Should().Be("alice");
    }

    [Fact]
    public void StrictMultiTenant_UnscopedAdministrativeRead_Succeeds()
    {
        var policy = Create(MemoryIsolationMode.StrictMultiTenant, out _);

        var scope = policy.ResolveReadScope(null, null, "AdminOp", MemoryOperationAccess.Administrative);

        scope.HasOwnerFilter.Should().BeFalse("administrative access is the explicit global-access path, never blocked");
    }

    [Fact]
    public void StrictMultiTenant_UnscopedAdministrativeWrite_Succeeds()
    {
        var policy = Create(MemoryIsolationMode.StrictMultiTenant, out _);

        policy.ResolveWriteOwner(null, "AdminOp", MemoryOperationAccess.Administrative).Should().BeNull();
    }
}
