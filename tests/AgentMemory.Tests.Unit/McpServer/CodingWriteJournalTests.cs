using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using AgentMemory.McpHost.Coding;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingWriteJournalTests
{
    private static readonly CodingIdentity Owner = new("owner", "alpha");

    [Fact]
    public void Reopened_journal_preserves_scoped_receipts_payloads_and_order()
    {
        using var directory = new PrivateDirectory();
        var first = Entry("one");
        var second = Entry("two");
        using (var journal = directory.Open()) { journal.Save(first); journal.Save(second); }
        using var reopened = directory.Open();
        var loaded = reopened.Load();
        loaded.Select(e => e.Job.JobId).Should().Equal(first.Job.JobId, second.Job.JobId);
        loaded[0].Should().BeEquivalentTo(first);
        reopened.CanResume(Owner).Should().BeTrue();
        reopened.CanResume(Owner with { ProjectId = "beta" }).Should().BeFalse();
        reopened.CanResume(Owner with { CanWrite = false }).Should().BeFalse();
    }

    [Fact]
    public void Completion_removes_conversation_from_journal_and_keeps_receipt_on_reopen()
    {
        using var directory = new PrivateDirectory();
        var original = Entry("one");
        var finished = original with { Request = null, Job = original.Job with
        {
            State = CodingJobState.Succeeded, CompletedAtUtc = DateTimeOffset.UtcNow,
        } };
        using (var journal = directory.Open()) { journal.Save(original); journal.Save(finished); }
        using (var journal = directory.Open()) journal.Load().Should().ContainSingle().Which.Should().BeEquivalentTo(finished);
        var bytes = File.ReadAllBytes(Path.Combine(directory.Path, "receipts.db"));
        System.Text.Encoding.UTF8.GetString(bytes).Should().NotContain("unique-capture-canary");
    }

    [Fact]
    public void Second_host_and_changed_deployment_binding_are_rejected()
    {
        using var directory = new PrivateDirectory();
        using (var first = directory.Open())
        {
            first.Save(Entry("one"));
            var second = () => directory.Open();
            second.Should().Throw<IOException>();
        }
        var changed = () => directory.Open("other-deployment");
        changed.Should().Throw<IOException>();
        using var original = directory.Open();
        original.Load().Should().ContainSingle();
    }

    [Fact]
    public void Full_retention_rolls_back_new_admission_without_losing_existing_receipts()
    {
        using var directory = new PrivateDirectory();
        using (var journal = directory.Open())
        {
            for (var i = 0; i < 64; i++) journal.Save(Entry(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            journal.Invoking(j => j.Save(Entry("overflow"))).Should().Throw<IOException>();
        }
        using var reopened = directory.Open();
        reopened.Load().Should().HaveCount(64);
    }

    [Fact]
    public void Malformed_journal_is_preserved_and_rejected_instead_of_reset()
    {
        using var directory = new PrivateDirectory();
        using (var journal = directory.Open()) journal.Save(Entry("one"));
        var path = System.IO.Path.Combine(directory.Path, "receipts.db");
        File.WriteAllBytes(path, "damaged receipt evidence"u8.ToArray());
        var open = () => directory.Open();
        open.Should().Throw<SqliteException>();
        File.ReadAllBytes(path).Should().Equal("damaged receipt evidence"u8.ToArray());
    }

    [Fact]
    public void Mismatched_payload_and_terminal_text_are_rejected()
    {
        using var directory = new PrivateDirectory();
        using var journal = directory.Open();
        var original = Entry("one");
        journal.Invoking(j => j.Save(original with { Hash = new string('b', 64) })).Should().Throw<IOException>();
        journal.Invoking(j => j.Save(original with { Job = original.Job with { State = CodingJobState.Succeeded } }))
            .Should().Throw<IOException>();
        journal.Load().Should().BeEmpty();
    }

    [Fact]
    public void Existing_public_directory_is_rejected_without_changing_its_permissions()
    {
        using var directory = new PrivateDirectory();
        if (OperatingSystem.IsWindows())
        {
            var info = Directory.CreateDirectory(directory.Path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            info.SetAccessControl(acl);
            var before = info.GetAccessControl().GetSecurityDescriptorBinaryForm();
            FluentActions.Invoking(() => directory.Open()).Should().Throw<ArgumentException>();
            info.GetAccessControl().GetSecurityDescriptorBinaryForm().Should().Equal(before);
            return;
        }
        Directory.CreateDirectory(directory.Path);
        File.SetUnixFileMode(directory.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        var open = () => directory.Open();
        open.Should().Throw<ArgumentException>();
        File.GetUnixFileMode(directory.Path).Should().HaveFlag(UnixFileMode.OtherRead);
    }

    [Fact]
    public void Default_receipt_and_tool_bytes_do_not_advertise_a_journal()
    {
        var job = Entry("one").Job with
        {
            JobId = "11111111111111111111111111111111", QueuedAtUtc = DateTimeOffset.UnixEpoch, Durability = null,
        };
        JsonSerializer.Serialize(job, CodingApi.Json).Should().Be(
            """{"jobId":"11111111111111111111111111111111","state":"queued","queuedAtUtc":"1970-01-01T00:00:00+00:00","completedAtUtc":null,"sourceMessageId":null,"errorCode":null,"extractionPath":"unknown"}""");
        CodingToolDefinitions.For(false).Should().BeSameAs(CodingToolDefinitions.All);
        var enabled = CodingToolDefinitions.For(true);
        enabled.Single(t => t.Name == "write_status").Description.Should().Contain("journal");
        enabled.Select(t => t.Name).Should().Equal(CodingToolDefinitions.All.Select(t => t.Name));
    }

    [Fact]
    public void Default_configuration_does_not_select_a_journal()
    {
        CodingConfiguration.Parse(CodingConfigurationTests.Json, CodingConfigurationTests.EnvironmentValues().GetValueOrDefault)
            .Options.WriteJournal.Should().BeNull();
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("")]
    public void Journal_path_must_be_explicit_and_absolute(string path)
    {
        new CodingJournalOptions { Directory = path }.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Windows_forward_slash_UNC_is_not_mistaken_for_a_local_journal()
    {
        if (!OperatingSystem.IsWindows()) return;
        new CodingJournalOptions { Directory = "//server/share/journal" }.Invoking(o => o.Validate())
            .Should().Throw<ArgumentException>();
    }

    private static CodingJournalEntry Entry(string turn)
    {
        var request = new CodingRememberRequest { Session = "session", TurnId = turn, UserText = "unique-capture-canary",
            Commit = new string('a', 40), Source = "synthetic" };
        return new()
        {
            Identity = Owner, Key = CodingInput.MessageId(CodingInput.SessionId(Owner, request.Session), turn, "user"),
            Hash = request.PayloadHash(), Request = request,
            Job = new CodingWriteJob { JobId = Guid.NewGuid().ToString("N"), State = CodingJobState.Queued,
                QueuedAtUtc = DateTimeOffset.UtcNow, Durability = "journal" },
        };
    }

    private sealed class PrivateDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coding-journal-" + Guid.NewGuid().ToString("N"));
        public CodingWriteJournal Open(string binding = "synthetic-deployment") => new(
            new CodingJournalOptions { Directory = Path, RetainedJobs = 64 }, binding,
            identity => identity.OwnerId == Owner.OwnerId && identity.ProjectId == Owner.ProjectId);
        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            foreach (var file in Directory.GetFiles(Path)) File.Delete(file);
            Directory.Delete(Path);
        }
    }
}
