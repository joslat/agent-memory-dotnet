//! The second and third status layers, and the result documents of `native/CONTRACT.md` as types.
//!
//! [`Polled`] is the poll disposition: whether a result is there yet. [`OperationStatus`] is the operation's own
//! outcome inside the result: a recall that timed out or found the store unreachable is a result with that status, not
//! an [`Error`](crate::Error).

use serde::{Deserialize, Deserializer};
use serde_json::{Map, Value};

use agentmemory_native_sys as sys;

use crate::Error;

/// A poll's disposition.
#[derive(Debug, Clone, PartialEq)]
// A poll's answer is matched at once and not stored, so the size difference costs nothing; a Box would cost a step.
#[allow(clippy::large_enum_variant)]
pub enum Polled {
    /// No result yet.
    Pending,
    /// The job's result.
    Terminal(Outcome),
}

/// The operation's own status, the `status` of a result document.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
#[non_exhaustive]
pub enum OperationStatus {
    /// It ran. For a recall, the items hold what it found, possibly nothing; for a write, the result is a receipt.
    Ok,
    /// The time allowed ran out.
    Timeout,
    /// The caller cancelled the job.
    Cancelled,
    /// The store (or the embedding endpoint, or the journal) could not be reached or refused.
    Unavailable,
    /// The request was well-formed JSON but cannot run.
    Invalid,
    /// Anything else; the error's code and message say what.
    Error,
    /// A status this binding does not know.
    Other(String),
}

impl From<String> for OperationStatus {
    fn from(text: String) -> OperationStatus {
        match text.as_str() {
            "ok" => OperationStatus::Ok,
            "timeout" => OperationStatus::Timeout,
            "cancelled" => OperationStatus::Cancelled,
            "unavailable" => OperationStatus::Unavailable,
            "invalid" => OperationStatus::Invalid,
            "error" => OperationStatus::Error,
            _ => OperationStatus::Other(text),
        }
    }
}

impl<'de> Deserialize<'de> for OperationStatus {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        String::deserialize(deserializer).map(OperationStatus::from)
    }
}

impl OperationStatus {
    /// The status as the contract writes it.
    pub fn as_str(&self) -> &str {
        match self {
            OperationStatus::Ok => "ok",
            OperationStatus::Timeout => "timeout",
            OperationStatus::Cancelled => "cancelled",
            OperationStatus::Unavailable => "unavailable",
            OperationStatus::Invalid => "invalid",
            OperationStatus::Error => "error",
            OperationStatus::Other(text) => text,
        }
    }
}

/// Why an operation did not end `ok`: every other status carries one.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[non_exhaustive]
pub struct OperationError {
    /// A stable code, for example `dimension_mismatch`, `reads_paused` or `store_unavailable`.
    pub code: String,
    /// A message for people.
    #[serde(default)]
    pub message: String,
}

/// How long the job waited for a slot and ran, in milliseconds.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
#[non_exhaustive]
pub struct Timing {
    /// Time in the queue.
    #[serde(default)]
    pub queued_ms: u64,
    /// Time running.
    #[serde(default)]
    pub run_ms: u64,
}

/// Any result document: the parts every result has, and the whole document.
#[derive(Debug, Clone, PartialEq)]
#[non_exhaustive]
pub struct Outcome {
    /// The operation's own status.
    pub status: OperationStatus,
    /// The operation, when the request named a known one.
    pub operation: Option<String>,
    /// The request's own correlation value, echoed.
    pub request_id: Option<String>,
    /// Present for every status other than `ok`.
    pub error: Option<OperationError>,
    /// Queue and run times.
    pub timing: Timing,
    /// The whole document, as the library wrote it.
    pub document: Value,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Head {
    status: OperationStatus,
    #[serde(default)]
    operation: Option<String>,
    #[serde(default)]
    request_id: Option<String>,
    #[serde(default)]
    error: Option<OperationError>,
    #[serde(default, deserialize_with = "null_as_default")]
    timing: Timing,
}

impl Outcome {
    /// Reads a result document.
    pub fn from_slice(bytes: &[u8]) -> Result<Outcome, Error> {
        let document: Value = serde_json::from_slice(bytes).map_err(|error| Error::Decode(error.to_string()))?;
        Outcome::from_value(document)
    }

    /// Reads a result document that is already JSON.
    pub fn from_value(document: Value) -> Result<Outcome, Error> {
        if !document.is_object() {
            return Err(Error::Decode("a result document is a JSON object".into()));
        }
        let head = Head::deserialize(&document).map_err(|error| Error::Decode(error.to_string()))?;
        Ok(Outcome {
            status: head.status,
            operation: head.operation,
            request_id: head.request_id,
            error: head.error,
            timing: head.timing,
            document,
        })
    }

    /// Whether the status is `ok`.
    pub fn is_ok(&self) -> bool {
        self.status == OperationStatus::Ok
    }

    fn parse<T: for<'de> Deserialize<'de>>(&self) -> Result<T, Error> {
        T::deserialize(&self.document).map_err(|error| Error::Decode(error.to_string()))
    }
}

/// A result type that can be read from an [`Outcome`].
pub trait FromOutcome: Sized {
    /// Reads the typed result.
    fn from_outcome(outcome: Outcome) -> Result<Self, Error>;
}

impl FromOutcome for Outcome {
    fn from_outcome(outcome: Outcome) -> Result<Outcome, Error> {
        Ok(outcome)
    }
}

fn null_as_default<'de, D: Deserializer<'de>, T: Default + Deserialize<'de>>(deserializer: D) -> Result<T, D::Error> {
    Option::<T>::deserialize(deserializer).map(Option::unwrap_or_default)
}

fn user() -> String {
    "user".into()
}

/// A fact: subject, predicate, object.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
#[non_exhaustive]
pub struct Fact {
    /// Its id.
    pub id: String,
    /// Who or what it is about.
    pub subject: String,
    /// The relation.
    pub predicate: String,
    /// The value.
    pub object: String,
    /// How sure the writer was, 0-1.
    pub confidence: f64,
    /// A category, when it has one.
    #[serde(default)]
    pub category: Option<String>,
    /// When it became true, when known.
    #[serde(default)]
    pub valid_from: Option<String>,
    /// When it stopped being true, when known.
    #[serde(default)]
    pub valid_until: Option<String>,
    /// When it was stored.
    #[serde(default)]
    pub created_at: Option<String>,
    /// Its metadata, exactly as stored.
    #[serde(default, deserialize_with = "null_as_default")]
    pub metadata: Map<String, Value>,
}

/// A preference.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
#[non_exhaustive]
pub struct Preference {
    /// Its id.
    pub id: String,
    /// Its category, for example `style`.
    pub category: String,
    /// The preference in words.
    pub text: String,
    /// When it applies, when it says.
    #[serde(default)]
    pub context: Option<String>,
    /// How sure the writer was, 0-1.
    pub confidence: f64,
    /// When it was stored.
    #[serde(default)]
    pub created_at: Option<String>,
    /// Its metadata, exactly as stored.
    #[serde(default, deserialize_with = "null_as_default")]
    pub metadata: Map<String, Value>,
}

/// A message of the request's session, in the owner's own words.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[non_exhaustive]
pub struct PastMessage {
    /// Its id.
    pub id: String,
    /// Who said it.
    #[serde(default = "user")]
    pub role: String,
    /// What was said.
    pub text: String,
    /// When, when known.
    #[serde(default)]
    pub at: Option<String>,
}

/// A person, place or thing.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[non_exhaustive]
pub struct Entity {
    /// Its id.
    pub id: String,
    /// Its name.
    pub name: String,
    /// Its type, for example `PERSON` (the document's `type`).
    #[serde(rename = "type", default)]
    pub kind: String,
    /// A description, when it has one.
    #[serde(default)]
    pub description: Option<String>,
}

/// How two entities relate.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[non_exhaustive]
pub struct Relationship {
    /// Its id.
    pub id: String,
    /// The first entity's name.
    pub source: String,
    /// The relation, for example `SISTER_OF` (the document's `type`).
    #[serde(rename = "type")]
    pub kind: String,
    /// The second entity's name.
    pub target: String,
    /// A description, when it has one.
    #[serde(default)]
    pub description: Option<String>,
}

/// The result of `memory_get_context`.
#[derive(Debug, Clone, PartialEq)]
#[non_exhaustive]
pub struct Recall {
    /// The operation's own status: `ok` means the recall ran; the lists hold what it found, possibly nothing.
    pub status: OperationStatus,
    /// The request's correlation value, echoed.
    pub request_id: Option<String>,
    /// Why it did not end `ok`.
    pub error: Option<OperationError>,
    /// The facts most like the question.
    pub facts: Vec<Fact>,
    /// The preferences most like the question.
    pub preferences: Vec<Preference>,
    /// The facts of a time the question names (`timeWindows`).
    pub window_facts: Vec<Fact>,
    /// The session's messages most like the question (`maxMessages`).
    pub messages: Vec<PastMessage>,
    /// The entities most like the question (`maxEntities`).
    pub entities: Vec<Entity>,
    /// How those relate (`maxRelationships`).
    pub relationships: Vec<Relationship>,
    /// Whether a list was cut to its limit.
    pub truncated: bool,
    /// Queue and run times.
    pub timing: Timing,
    /// The whole document.
    pub document: Value,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RecallBody {
    #[serde(default, deserialize_with = "null_as_default")]
    facts: Vec<Fact>,
    #[serde(default, deserialize_with = "null_as_default")]
    preferences: Vec<Preference>,
    #[serde(default, deserialize_with = "null_as_default")]
    window_facts: Vec<Fact>,
    #[serde(default, deserialize_with = "null_as_default")]
    messages: Vec<PastMessage>,
    #[serde(default, deserialize_with = "null_as_default")]
    entities: Vec<Entity>,
    #[serde(default, deserialize_with = "null_as_default")]
    relationships: Vec<Relationship>,
    #[serde(default, deserialize_with = "null_as_default")]
    truncated: bool,
}

impl Recall {
    /// Whether the recall ran.
    pub fn is_ok(&self) -> bool {
        self.status == OperationStatus::Ok
    }
}

impl FromOutcome for Recall {
    fn from_outcome(outcome: Outcome) -> Result<Recall, Error> {
        let body: RecallBody = outcome.parse()?;
        Ok(Recall {
            status: outcome.status,
            request_id: outcome.request_id,
            error: outcome.error,
            facts: body.facts,
            preferences: body.preferences,
            window_facts: body.window_facts,
            messages: body.messages,
            entities: body.entities,
            relationships: body.relationships,
            truncated: body.truncated,
            timing: outcome.timing,
            document: outcome.document,
        })
    }
}

/// Where a journal operation stands.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
#[non_exhaustive]
pub enum ReceiptState {
    /// Durable, waiting for the writer.
    Accepted,
    /// The writer is working on it.
    Running,
    /// Done and verified. Only this state supports saying "remembered".
    Applied,
    /// It failed; the store shows nothing recall reads came from it.
    Rejected,
    /// Some of it was written, but not everything succeeded or verified.
    Partial,
    /// The store failed while writing, or the process ended while it ran. Never run again.
    Unknown,
    /// A partial or unknown write resolved by keeping what it wrote.
    Kept,
    /// Forgotten or erased before it ran: it never wrote anything.
    Cancelled,
    /// A remember whose memories a later forget closed.
    Forgotten,
    /// The owner was erased since.
    Erased,
    /// A state this binding does not know.
    Other(String),
}

impl<'de> Deserialize<'de> for ReceiptState {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let text = String::deserialize(deserializer)?;
        Ok(match text.as_str() {
            "accepted" => ReceiptState::Accepted,
            "running" => ReceiptState::Running,
            "applied" => ReceiptState::Applied,
            "rejected" => ReceiptState::Rejected,
            "partial" => ReceiptState::Partial,
            "unknown" => ReceiptState::Unknown,
            "kept" => ReceiptState::Kept,
            "cancelled" => ReceiptState::Cancelled,
            "forgotten" => ReceiptState::Forgotten,
            "erased" => ReceiptState::Erased,
            _ => ReceiptState::Other(text),
        })
    }
}

fn remember() -> String {
    "remember".into()
}

/// Where one journal operation stands: a remember, a forget or an owner erase (`kind`).
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
#[non_exhaustive]
pub struct Receipt {
    /// The operation's id.
    pub operation_id: String,
    /// `remember`, `forget` or `erase`.
    #[serde(default = "remember")]
    pub kind: String,
    /// The host's id for the spoken item and revision.
    #[serde(default)]
    pub source_id: Option<String>,
    /// `explicit` or `observed`, for a remember.
    #[serde(default)]
    pub origin: Option<String>,
    /// The remember a forget names.
    #[serde(default)]
    pub target_operation_id: Option<String>,
    /// Whether this request found the operation already accepted, and ran nothing.
    #[serde(default)]
    pub duplicate: bool,
    /// Where it stands.
    pub state: ReceiptState,
    /// Why, for a rejected or partial operation, or what a forget did not find.
    #[serde(default)]
    pub reason: Option<String>,
    /// Whether recall can find at least one memory it wrote.
    #[serde(default)]
    pub searchable: bool,
    /// When the words were said.
    #[serde(default)]
    pub occurred_at: Option<String>,
    /// When it was accepted into the journal.
    #[serde(default)]
    pub accepted_at: Option<String>,
    /// When the writer started it.
    #[serde(default)]
    pub started_at: Option<String>,
    /// When it ended.
    #[serde(default)]
    pub finished_at: Option<String>,
    /// The facts it produced.
    #[serde(default, deserialize_with = "null_as_default")]
    pub facts: Vec<String>,
    /// The preferences it produced.
    #[serde(default, deserialize_with = "null_as_default")]
    pub preferences: Vec<String>,
    /// The stored memories it closed.
    #[serde(default, deserialize_with = "null_as_default")]
    pub closed: Vec<String>,
    /// How many nodes an erase deleted.
    #[serde(default)]
    pub deleted: Option<u64>,
}

impl Receipt {
    /// Applied and searchable: the only receipt that supports saying "remembered".
    pub fn remembered(&self) -> bool {
        self.state == ReceiptState::Applied && self.searchable
    }

    /// No longer accepted or running.
    pub fn ended(&self) -> bool {
        !matches!(self.state, ReceiptState::Accepted | ReceiptState::Running)
    }
}

/// The result of `memory_remember`, `memory_operation_get`, `memory_operation_resolve`, `memory_forget` and
/// `memory_erase_owner`. Status `ok` means a receipt came back (read its state); any other status means nothing was
/// accepted by this request.
#[derive(Debug, Clone, PartialEq)]
#[non_exhaustive]
pub struct WriteResult {
    /// The operation's own status.
    pub status: OperationStatus,
    /// The request's correlation value, echoed.
    pub request_id: Option<String>,
    /// Why it did not end `ok`.
    pub error: Option<OperationError>,
    /// The receipt, with status `ok`.
    pub receipt: Option<Receipt>,
    /// Queue and run times.
    pub timing: Timing,
    /// The whole document.
    pub document: Value,
}

#[derive(Deserialize)]
struct WriteBody {
    #[serde(default)]
    receipt: Option<Receipt>,
}

impl WriteResult {
    /// Whether a receipt came back.
    pub fn is_ok(&self) -> bool {
        self.status == OperationStatus::Ok
    }

    /// Whether the receipt says applied and searchable.
    pub fn remembered(&self) -> bool {
        self.receipt.as_ref().is_some_and(Receipt::remembered)
    }
}

impl FromOutcome for WriteResult {
    fn from_outcome(outcome: Outcome) -> Result<WriteResult, Error> {
        let body: WriteBody = outcome.parse()?;
        Ok(WriteResult {
            status: outcome.status,
            request_id: outcome.request_id,
            error: outcome.error,
            receipt: body.receipt,
            timing: outcome.timing,
            document: outcome.document,
        })
    }
}

/// The ABI version a library reports.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[non_exhaustive]
pub struct AbiVersion {
    /// Major version.
    pub major: u32,
    /// Minor version.
    pub minor: u32,
}

/// The result of `capabilities_get`: what this context offers.
#[derive(Debug, Clone, PartialEq)]
#[non_exhaustive]
pub struct Capabilities {
    /// The operation's own status.
    pub status: OperationStatus,
    /// Why it did not end `ok`.
    pub error: Option<OperationError>,
    /// The library's version.
    pub library_version: Option<String>,
    /// The library's ABI.
    pub abi: Option<AbiVersion>,
    /// `read_only`, `explicit_write`, `learning` or `admin`.
    pub profile: Option<String>,
    /// The operations this context accepts.
    pub operations: Vec<String>,
    /// ABI 1.1: the optional exports and request properties this context accepts. Empty from a 1.0 library.
    pub features: Vec<String>,
    /// The embedding's dimensions.
    pub embedding_dimensions: Option<u32>,
    /// Whether the library embeds text itself (an embedding endpoint is configured).
    pub generates_embeddings: bool,
    /// The whole document.
    pub document: Value,
}

impl Capabilities {
    /// Whether the context accepts this operation.
    pub fn offers(&self, operation: &str) -> bool {
        self.operations.iter().any(|offered| offered == operation)
    }

    /// Whether the context has this ABI 1.1 feature.
    pub fn has_feature(&self, feature: &str) -> bool {
        self.features.iter().any(|offered| offered == feature)
    }
}

#[derive(Deserialize)]
struct LibraryInfo {
    #[serde(default)]
    version: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct EmbeddingInfo {
    #[serde(default)]
    dimensions: Option<u32>,
    #[serde(default)]
    generates_embeddings: bool,
}

#[derive(Deserialize)]
struct CapabilitiesBody {
    #[serde(default)]
    library: Option<LibraryInfo>,
    #[serde(default)]
    abi: Option<AbiVersion>,
    #[serde(default)]
    profile: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    operations: Vec<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    features: Vec<String>,
    #[serde(default)]
    embedding: Option<EmbeddingInfo>,
}

impl FromOutcome for Capabilities {
    fn from_outcome(outcome: Outcome) -> Result<Capabilities, Error> {
        let body: CapabilitiesBody = outcome.parse()?;
        Ok(Capabilities {
            status: outcome.status,
            error: outcome.error,
            library_version: body.library.and_then(|library| library.version),
            abi: body.abi,
            profile: body.profile,
            operations: body.operations,
            features: body.features,
            embedding_dimensions: body.embedding.as_ref().and_then(|embedding| embedding.dimensions),
            generates_embeddings: body.embedding.is_some_and(|embedding| embedding.generates_embeddings),
            document: outcome.document,
        })
    }
}

/// One coherent snapshot of a context's revisions and flags (`am_v1_state`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[non_exhaustive]
pub struct ContextState {
    /// The state revision.
    pub state_revision: u64,
    /// The policy epoch.
    pub policy_epoch: u64,
    /// The deletion epoch; each owner erase that starts raises it.
    pub deletion_epoch: u64,
    /// The memory revision.
    pub memory_revision: u64,
    /// The raw flags.
    pub flags: u32,
}

impl ContextState {
    /// Recall may run (no write is pending or uncertain, in the profiles that pause it).
    pub fn read_allowed(&self) -> bool {
        self.flags & sys::AM_V1_READ_ALLOWED != 0
    }

    /// Writes are admitted. Only the write profiles set it; a `read_only` context never does.
    pub fn admission_allowed(&self) -> bool {
        self.flags & sys::AM_V1_ADMISSION_ALLOWED != 0
    }

    /// No write is accepted or running.
    pub fn write_quiescent(&self) -> bool {
        self.flags & sys::AM_V1_WRITE_QUIESCENT != 0
    }

    /// The context is closing.
    pub fn closing(&self) -> bool {
        self.flags & sys::AM_V1_CLOSING_FLAG != 0
    }

    /// An owner erase is pending.
    pub fn erase_pending(&self) -> bool {
        self.flags & sys::AM_V1_ERASE_PENDING != 0
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn a_recall_reads_its_items_and_keeps_the_document() {
        let document = json!({
            "schemaVersion": 1, "operation": "memory_get_context", "requestId": "turn-17/1", "status": "ok",
            "facts": [{"id": "f-1", "subject": "user", "predicate": "works_in", "object": "Zurich", "confidence": 0.9,
                       "category": null, "validFrom": null, "validUntil": null,
                       "createdAt": "2026-11-20T01:15:00+00:00", "metadata": {"trust_level": "verified"}}],
            "preferences": [{"id": "p-1", "category": "style", "text": "Prefers concise answers", "context": null,
                             "confidence": 0.9, "createdAt": "2026-11-20T01:15:00+00:00", "metadata": {}}],
            "windowFacts": [],
            "messages": [{"id": "m-1", "role": "user", "text": "Yesterday I had dinner.", "at": null}],
            "entities": [{"id": "e-1", "name": "Sam", "type": "PERSON", "description": null}],
            "relationships": [{"id": "r-1", "source": "Sam", "type": "SISTER_OF", "target": "user", "description": null}],
            "truncated": false, "timing": {"queuedMs": 0, "runMs": 31}
        });
        let outcome = Outcome::from_value(document.clone()).unwrap();
        assert!(outcome.is_ok());
        assert_eq!(outcome.timing.run_ms, 31);
        let recall = Recall::from_outcome(outcome).unwrap();
        assert_eq!(recall.request_id.as_deref(), Some("turn-17/1"));
        assert_eq!(recall.facts[0].object, "Zurich");
        assert_eq!(recall.facts[0].metadata["trust_level"], "verified");
        assert_eq!(recall.preferences[0].text, "Prefers concise answers");
        assert_eq!(recall.entities[0].kind, "PERSON");
        assert_eq!(recall.relationships[0].kind, "SISTER_OF");
        assert_eq!(recall.document, document);
    }

    #[test]
    fn a_failed_recall_is_a_result_with_its_status_not_an_error() {
        let outcome = Outcome::from_slice(
            br#"{"schemaVersion":1,"operation":"memory_get_context","status":"unavailable",
                 "error":{"code":"store_unavailable","message":"no store"},"timing":{"queuedMs":1,"runMs":2}}"#,
        )
        .unwrap();
        let recall = Recall::from_outcome(outcome).unwrap();
        assert_eq!(recall.status, OperationStatus::Unavailable);
        assert_eq!(recall.error.unwrap().code, "store_unavailable");
        assert!(recall.facts.is_empty());
    }

    #[test]
    fn unknown_statuses_and_states_are_kept() {
        let outcome =
            Outcome::from_slice(br#"{"status":"paused","receipt":{"operationId":"op-1","state":"later"}}"#).unwrap();
        assert_eq!(outcome.status, OperationStatus::Other("paused".into()));
        let write = WriteResult::from_outcome(outcome).unwrap();
        let receipt = write.receipt.unwrap();
        assert_eq!(receipt.state, ReceiptState::Other("later".into()));
        assert_eq!(receipt.kind, "remember");
        assert!(!receipt.remembered());
    }

    #[test]
    fn a_receipt_is_remembered_only_when_applied_and_searchable() {
        let read = |state: &str, searchable: bool| {
            let document = json!({"status": "ok", "receipt": {"operationId": "op-1", "kind": "remember",
                "sourceId": "s/1", "origin": "explicit", "duplicate": false, "state": state, "reason": null,
                "searchable": searchable, "occurredAt": "2026-11-20T10:00:00Z", "acceptedAt": "2026-11-20T10:00:01Z",
                "startedAt": null, "finishedAt": null, "facts": [], "preferences": ["p-1"], "closed": [],
                "deleted": null}});
            WriteResult::from_outcome(Outcome::from_value(document).unwrap()).unwrap()
        };
        assert!(read("applied", true).remembered());
        assert!(!read("applied", false).remembered());
        assert!(!read("partial", true).remembered());
        assert!(read("accepted", true).receipt.is_some_and(|receipt| !receipt.ended()));
    }

    #[test]
    fn capabilities_name_operations_and_features() {
        let document = json!({"schemaVersion": 1, "operation": "capabilities_get", "status": "ok",
            "library": {"name": "agentmemory_native", "version": "0.2.0"}, "abi": {"major": 1, "minor": 1},
            "profile": "read_only", "owner": {"mode": "fixed"},
            "embedding": {"dimensions": 8, "model": null, "generatesEmbeddings": false},
            "operations": ["capabilities_get", "memory_get_context"], "features": ["am_v1_wait"]});
        let capabilities = Capabilities::from_outcome(Outcome::from_value(document).unwrap()).unwrap();
        assert!(capabilities.offers("memory_get_context"));
        assert!(!capabilities.offers("memory_remember"));
        assert!(capabilities.has_feature("am_v1_wait"));
        assert_eq!(capabilities.abi, Some(AbiVersion { major: 1, minor: 1 }));
        assert_eq!(capabilities.embedding_dimensions, Some(8));
        assert_eq!(capabilities.library_version.as_deref(), Some("0.2.0"));
    }

    #[test]
    fn what_is_not_a_result_document_is_a_decode_error() {
        assert!(matches!(Outcome::from_slice(b"[1,2]"), Err(Error::Decode(_))));
        assert!(matches!(
            Outcome::from_slice(b"{\"no\":\"status\"}"),
            Err(Error::Decode(_))
        ));
        assert!(matches!(Outcome::from_slice(b"\xff"), Err(Error::Decode(_))));
    }

    #[test]
    fn state_flags_read_one_bit_each() {
        let state = ContextState {
            state_revision: 1,
            policy_epoch: 0,
            deletion_epoch: 0,
            memory_revision: 0,
            flags: sys::AM_V1_READ_ALLOWED | sys::AM_V1_CLOSING_FLAG,
        };
        assert!(state.read_allowed() && state.closing());
        assert!(!state.admission_allowed() && !state.write_quiescent() && !state.erase_pending());
    }
}
