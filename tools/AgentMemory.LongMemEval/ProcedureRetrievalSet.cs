namespace AgentMemory.LongMemEval;

/// <summary>One procedure to store: an id, the task it solves, and the ordering that solved it.</summary>
internal sealed record ProcedureFixture(string Id, string Task, string Outcome);

/// <summary>
/// One query: the task text an agent would ask with, and the procedure ids that would be correct.
/// An <b>empty</b> expectation means abstaining is the right answer.
/// </summary>
internal sealed record ProcedureQuery(string TaskId, string Query, IReadOnlyList<string> Correct);

/// <summary>
/// 26.2. <c>LME_Procedural</c> — a labelled task→procedure set, so procedural <b>retrieval</b> can be
/// measured independently of whether following a procedure happens to save a tool call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second procedural instrument.</b> The benefit harness answers "does using a procedure
/// help?" on one task with one model. It cannot answer "does the retriever return the <i>right</i>
/// procedure?", and those come apart in the dangerous direction: an agent with no procedural memory
/// investigates, while an agent with the <b>wrong</b> procedure executes — confidently, on a plan
/// built for a different task. A change that raises hit-rate while raising the wrong-procedure rate
/// improves every efficiency measure it has.
/// </para>
/// <para>
/// <b>A third of the queries have no correct answer, on purpose.</b> Without them, abstention is
/// unmeasurable and a retriever that always answers scores identically to one that knows when to stay
/// quiet. These are the cases that make <c>WrongProcedureRate</c> mean something.
/// </para>
/// <para>
/// <b>Near-misses are deliberate.</b> Several distractor procedures share vocabulary with a query but
/// solve a different task — "cancel a booking" against "book a connection", "rotate a key" against
/// "revoke a key". Retrieval that keys on surface similarity fails exactly here, which is the point:
/// a set where every wrong answer is obviously wrong measures nothing.
/// </para>
/// <para>
/// <b>Never reported as accuracy.</b> <see cref="ProcedureRetrievalPrecision"/> emits correct / wrong /
/// abstained, and abstention is not a failure. Collapsing the three into one percentage is the metric
/// substitution this whole track exists to avoid.
/// </para>
/// </remarks>
internal static class ProcedureRetrievalSet
{
    /// <summary>The procedures stored before any query runs.</summary>
    internal static IReadOnlyList<ProcedureFixture> Procedures { get; } =
    [
        new("proc-book-rail", "Book a rail connection for a traveller with a loyalty tier",
            "LookUpTraveller then CheckServiceBulletin then PlaceHold then Book"),
        new("proc-cancel-rail", "Cancel a rail booking and refund the traveller",
            "FindBooking then CheckRefundWindow then ReleaseSeat then IssueRefund"),
        new("proc-rebook-rail", "Move a traveller to a later rail departure after a disruption",
            "FindBooking then CheckServiceBulletin then PlaceHold then SwapSegment"),

        new("proc-revoke-key", "Revoke a compromised API key without breaking live traffic",
            "ListKeys then MintReplacement then DrainTraffic then RevokeOld"),
        new("proc-rotate-key", "Rotate an API key on the normal ninety-day schedule",
            "ListKeys then MintReplacement then UpdateConsumers then RevokeOld"),

        new("proc-restore-db", "Restore a database from last night's backup",
            "StopWrites then LocateSnapshot then RestoreSnapshot then ReplayWal then ResumeWrites"),
        new("proc-failover-db", "Fail a database over to its replica during an incident",
            "CheckReplicaLag then FencePrimary then PromoteReplica then RepointClients"),

        new("proc-onboard-user", "Onboard a new employee into the internal systems",
            "CreateIdentity then AssignGroups then GrantBaseline then SendWelcome"),
        new("proc-offboard-user", "Offboard a departing employee",
            "SuspendIdentity then RevokeSessions then TransferOwnership then ArchiveMailbox"),

        new("proc-release", "Ship a patch release to production",
            "CutBranch then RunSuite then TagVersion then Publish then Announce"),
        new("proc-rollback", "Roll back a bad production release",
            "IdentifyBadVersion then RepointTraffic then RepublishPrevious then FileIncident"),

        new("proc-expense", "Submit a travel expense claim over the approval threshold",
            "AttachReceipts then ClassifyCategory then RequestManagerApproval then Submit"),
    ];

    /// <summary>
    /// The queries. Twenty: fourteen answerable — several against near-miss distractors — and six that
    /// should abstain because nothing stored solves them.
    /// </summary>
    internal static IReadOnlyList<ProcedureQuery> Queries { get; } =
    [
        // ── direct restatements ────────────────────────────────────────────────
        new("q01", "Book the 14:05 rail connection for a traveller with a loyalty tier", ["proc-book-rail"]),
        new("q02", "Restore the database from the backup taken last night", ["proc-restore-db"]),
        new("q03", "Offboard an employee who is leaving on Friday", ["proc-offboard-user"]),
        new("q04", "Ship a patch release to production", ["proc-release"]),

        // ── paraphrases: same task, different words ────────────────────────────
        new("q05", "A traveller needs a seat on the afternoon train and has status with us", ["proc-book-rail"]),
        new("q06", "Bring up the standby database because the primary is failing", ["proc-failover-db"]),
        new("q07", "A new starter joins on Monday and needs their accounts", ["proc-onboard-user"]),
        new("q08", "The version we just deployed is broken and must come out", ["proc-rollback"]),

        // ── near-misses: the wrong sibling is lexically closer ─────────────────
        // A key that LEAKED is not a key on a schedule; the safe ordering drains traffic before
        // revoking. A retriever keying on "API key" alone picks the rotation procedure and an agent
        // following it revokes a live credential.
        new("q09", "An API key was posted publicly and must be killed off safely", ["proc-revoke-key"]),
        new("q10", "It is the ninety-day mark and this key is due for its routine change", ["proc-rotate-key"]),
        // "Rail booking" matches three procedures; only one is about undoing one.
        new("q11", "The traveller no longer wants the trip and wants their money back", ["proc-cancel-rail"]),
        // Disruption rebooking shares almost every word with both booking and cancelling.
        new("q12", "Storms cancelled the service, put the traveller on a later departure", ["proc-rebook-rail"]),

        // ── either-of: two orderings are legitimately acceptable ───────────────
        new("q13", "Replace this API key with a new one", ["proc-rotate-key", "proc-revoke-key"]),
        new("q14", "Get the database serving again after the outage", ["proc-restore-db", "proc-failover-db"]),

        // ── abstain: nothing stored solves these ───────────────────────────────
        // Each is adjacent to a stored procedure in vocabulary and unrelated in task, so answering
        // requires the retriever to have been fooled rather than merely unlucky.
        new("q15", "Negotiate a discount with the rail operator for bulk travel", []),
        new("q16", "Decide which database vendor to migrate to next year", []),
        new("q17", "Write the quarterly engineering hiring plan", []),
        new("q18", "Explain to a customer why their release was delayed", []),
        new("q19", "Choose a new expense-management vendor", []),
        new("q20", "Design the on-call rota for the next quarter", []),
    ];
}
