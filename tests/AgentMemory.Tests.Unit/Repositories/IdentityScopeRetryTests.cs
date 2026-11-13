using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class IdentityScopeRetryTests
{
    private static TransientException Deadlock() => new("Neo.TransientError.Transaction.DeadlockDetected", "injected acquisition deadlock");
    private sealed class Fixture
    {
        public readonly INeo4jTransactionRunner Runner = Substitute.For<INeo4jTransactionRunner, INeo4jAtomicTransactionRunner>();
        public int Attempts, Callbacks, Acquisitions;
        public Func<Task>? Acquire;
        public Func<Task<int>>? Body;
        public bool FailCommit, FailRollback;
        public readonly Neo4jIdentityDecisionRepository Repository;
        public Fixture()
        {
            var options = new Neo4jOptions(); options.Extensions.Add("identity-evidence");
            Repository = new(Runner, Options.Create(options));
            Runner.WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
                .Returns(_ => { Acquisitions++; return Acquire?.Invoke() ?? Task.CompletedTask; });
            ((INeo4jAtomicTransactionRunner)Runner).ExecuteAtomicWriteAsync(Arg.Any<Func<CancellationToken, Task<int>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Atomic(call.Arg<Func<CancellationToken, Task<int>>>(), call.Arg<CancellationToken>()));
        }
        private async Task<int> Atomic(Func<CancellationToken, Task<int>> body, CancellationToken cancellationToken)
        {
            Attempts++;
            try { var result = await body(cancellationToken); if (FailCommit) throw Deadlock(); return result; }
            catch (Exception ex) when (FailRollback) { throw new AggregateException("rollback unconfirmed", ex); }
        }
        public Task<int> Execute(CancellationToken cancellationToken = default) => Repository.ExecuteScopedAsync("pablito", false, ["root"],
            _ => { Callbacks++; return Body?.Invoke() ?? Task.FromResult(42); }, cancellationToken);
    }
    [Fact]
    public async Task Two_pre_callback_deadlocks_retry_without_replaying_ingestion()
    {
        var f = new Fixture(); f.Acquire = () => f.Acquisitions <= 2 ? Task.FromException(Deadlock()) : Task.CompletedTask;
        (await f.Execute()).Should().Be(42); f.Attempts.Should().Be(3); f.Callbacks.Should().Be(1);
    }
    [Fact]
    public async Task Repeated_acquisition_failure_is_bounded_to_three_attempts()
    {
        var f = new Fixture { Acquire = () => Task.FromException(Deadlock()) };
        var action = () => f.Execute(); await action.Should().ThrowAsync<TransientException>();
        f.Attempts.Should().Be(3); f.Callbacks.Should().Be(0);
    }
    [Fact]
    public async Task A_transient_error_inside_the_callback_never_replays_side_effects()
    {
        var f = new Fixture { Body = () => Task.FromException<int>(Deadlock()) };
        var action = () => f.Execute(); await action.Should().ThrowAsync<TransientException>();
        f.Attempts.Should().Be(1); f.Callbacks.Should().Be(1);
    }
    [Fact]
    public async Task A_lost_or_failed_commit_never_replays_the_callback()
    {
        var f = new Fixture { FailCommit = true };
        var action = () => f.Execute(); await action.Should().ThrowAsync<TransientException>();
        f.Attempts.Should().Be(1); f.Callbacks.Should().Be(1);
    }
    [Fact]
    public async Task Unconfirmed_rollback_never_retries_even_before_callback()
    {
        var f = new Fixture { FailRollback = true, Acquire = () => Task.FromException(Deadlock()) };
        var action = () => f.Execute(); await action.Should().ThrowAsync<AggregateException>();
        f.Attempts.Should().Be(1); f.Callbacks.Should().Be(0);
    }
    [Fact]
    public async Task Nontransient_acquisition_errors_do_not_retry()
    {
        var f = new Fixture { Acquire = () => Task.FromException(new InvalidOperationException("invalid scope")) };
        var action = () => f.Execute(); await action.Should().ThrowAsync<InvalidOperationException>();
        f.Attempts.Should().Be(1); f.Callbacks.Should().Be(0);
    }
    [Fact]
    public async Task Cancellation_during_acquisition_or_backoff_stops_without_callback()
    {
        using var c = new CancellationTokenSource();
        var f = new Fixture { Acquire = () => { c.Cancel(); return Task.FromException(Deadlock()); } };
        var action = () => f.Execute(c.Token); await action.Should().ThrowAsync<OperationCanceledException>();
        f.Attempts.Should().Be(1); f.Callbacks.Should().Be(0);
    }
    [Fact]
    public async Task Already_cancelled_work_opens_no_transaction()
    {
        var f = new Fixture(); var action = () => f.Execute(new CancellationToken(true));
        await action.Should().ThrowAsync<OperationCanceledException>(); f.Attempts.Should().Be(0); f.Callbacks.Should().Be(0);
    }

    [Fact]
    public async Task An_owned_callback_cannot_widen_its_preacquired_shared_scope()
    {
        var f = new Fixture();
        f.Body = async () =>
        {
            var request = AgentMemory.Tests.Unit.Resolution.IdentityDecisionPayloadTests.Input() with { IncludeShared = true };
            await f.Repository.SaveAsync(request, "pablito"); return 42;
        };
        var action = () => f.Execute();
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*scope*");
        f.Callbacks.Should().Be(1); f.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task An_owned_callback_can_save_within_its_preacquired_scope()
    {
        var f = new Fixture();
        f.Body = async () =>
        {
            await f.Repository.SaveAsync(AgentMemory.Tests.Unit.Resolution.IdentityDecisionPayloadTests.Input(), "pablito");
            return 42;
        };
        (await f.Execute()).Should().Be(42); f.Callbacks.Should().Be(1); f.Attempts.Should().Be(1);
    }

    [Fact]
    public void A_captured_execution_context_cannot_reuse_an_expired_lease()
    {
        var runner = Substitute.For<INeo4jTransactionRunner>();
        ExecutionContext? captured;
        using (IdentityWriteScope.Enter(runner, ["owned:pablito"]))
        {
            IdentityWriteScope.Reuse(runner, ["owned:pablito"]).Should().BeTrue();
            captured = ExecutionContext.Capture();
        }
        ExecutionContext.Run(captured!, _ =>
        {
            var action = () => IdentityWriteScope.Reuse(runner, ["owned:pablito"]);
            action.Should().Throw<InvalidOperationException>().WithMessage("*outlive*");
        }, null);
        IdentityWriteScope.Reuse(runner, ["owned:pablito"]).Should().BeFalse();
    }

    [Fact]
    public void A_callback_cannot_write_through_a_different_transaction_runner()
    {
        var owner = Substitute.For<INeo4jTransactionRunner>();
        var other = Substitute.For<INeo4jTransactionRunner>();
        using var lease = IdentityWriteScope.Enter(owner, ["owned:pablito"]);
        var action = () => IdentityWriteScope.Reuse(other, ["owned:pablito"]);
        action.Should().Throw<InvalidOperationException>().WithMessage("*write scope*");
        IdentityWriteScope.Reuse(owner, ["owned:pablito"]).Should().BeTrue();
    }

    [Fact]
    public async Task An_independent_execution_flow_cannot_borrow_another_flows_scope_lease()
    {
        var runner = Substitute.For<INeo4jTransactionRunner>();
        using var lease = IdentityWriteScope.Enter(runner, ["owned:pablito"]);
        Task<bool> unrelated;
        using (ExecutionContext.SuppressFlow())
            unrelated = Task.Run(() => IdentityWriteScope.Reuse(runner, ["owned:pablito"]));
        (await unrelated).Should().BeFalse();
        IdentityWriteScope.Reuse(runner, ["owned:pablito"]).Should().BeTrue();
    }
}
