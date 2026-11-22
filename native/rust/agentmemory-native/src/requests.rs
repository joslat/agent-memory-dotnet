//! The requests of `native/CONTRACT.md`. Each builds the exact JSON document the library reads; optional sections are
//! sent only when asked for, so a request stays one an older library accepts.

use serde_json::{json, Map, Value};

use crate::results::{Capabilities, FromOutcome, Outcome, Recall, WriteResult};
use crate::Error;

/// One operation: its request document, its job time, and the type of its result.
pub trait Operation {
    /// The typed result.
    type Output: FromOutcome;

    /// The request document.
    fn request(&self) -> Result<Value, Error>;

    /// The job's time in the library, in milliseconds; 0 means the context's `defaultTimeoutMs`.
    fn timeout_ms(&self) -> u32 {
        0
    }
}

fn base(operation: &str) -> Map<String, Value> {
    let mut request = Map::new();
    request.insert("schemaVersion".into(), json!(1));
    request.insert("operation".into(), json!(operation));
    request
}

fn put_some(request: &mut Map<String, Value>, key: &str, value: Option<&str>) {
    if let Some(value) = value {
        request.insert(key.into(), json!(value));
    }
}

/// Any request document, sent as it is; the result is an [`Outcome`].
#[derive(Debug, Clone, PartialEq)]
pub struct Request {
    /// The document.
    pub document: Value,
    /// The job's time in the library (0: the context's default).
    pub timeout_ms: u32,
}

impl Operation for Request {
    type Output = Outcome;

    fn request(&self) -> Result<Value, Error> {
        Ok(self.document.clone())
    }

    fn timeout_ms(&self) -> u32 {
        self.timeout_ms
    }
}

/// `capabilities_get`: what the context offers.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CapabilitiesGet;

impl Operation for CapabilitiesGet {
    type Output = Capabilities;

    fn request(&self) -> Result<Value, Error> {
        Ok(Value::Object(base("capabilities_get")))
    }
}

/// `memory_get_context`: owner-scoped recall of facts and preferences, and the sections asked for.
///
/// Exactly one of `query` (text the library embeds; needs an embedding endpoint in the configuration) and
/// `query_vector` (made by the caller, with the configured number of dimensions).
#[derive(Debug, Clone, PartialEq)]
pub struct GetContext {
    /// The session; `maxMessages` reads this session's messages only.
    pub session_id: String,
    /// The question as text.
    pub query: Option<String>,
    /// The question as a vector.
    pub query_vector: Option<Vec<f32>>,
    /// The owner, in the `per_request` owner mode only.
    pub owner: Option<String>,
    /// 0-50 (default 5).
    pub max_facts: u32,
    /// 0-50 (default 5).
    pub max_preferences: u32,
    /// 0-1; the library's default is 0.7. Scores are `(1 + cosine) / 2`.
    pub min_score: Option<f64>,
    /// The caller's correlation value, echoed in the result.
    pub request_id: Option<String>,
    /// 0-20: the session's messages most like the question.
    pub max_messages: u32,
    /// 0-50: the entities most like the question.
    pub max_entities: u32,
    /// 0-50: how those relate.
    pub max_relationships: u32,
    /// The facts of a time the question names.
    pub time_windows: bool,
    /// 0-32: the live facts of someone a recalled fact connects the owner to.
    pub max_connection_facts: u32,
    /// The job's time in the library (0: the context's default).
    pub timeout_ms: u32,
}

impl GetContext {
    fn new(session_id: impl Into<String>) -> GetContext {
        GetContext {
            session_id: session_id.into(),
            query: None,
            query_vector: None,
            owner: None,
            max_facts: 5,
            max_preferences: 5,
            min_score: None,
            request_id: None,
            max_messages: 0,
            max_entities: 0,
            max_relationships: 0,
            time_windows: false,
            max_connection_facts: 0,
            timeout_ms: 0,
        }
    }

    /// A recall by text, which the library embeds.
    pub fn query(session_id: impl Into<String>, text: impl Into<String>) -> GetContext {
        GetContext {
            query: Some(text.into()),
            ..GetContext::new(session_id)
        }
    }

    /// A recall by a vector the caller made.
    pub fn vector(session_id: impl Into<String>, vector: impl Into<Vec<f32>>) -> GetContext {
        GetContext {
            query_vector: Some(vector.into()),
            ..GetContext::new(session_id)
        }
    }
}

impl Operation for GetContext {
    type Output = Recall;

    fn request(&self) -> Result<Value, Error> {
        let mut request = base("memory_get_context");
        request.insert("sessionId".into(), json!(self.session_id));
        request.insert("maxFacts".into(), json!(self.max_facts));
        request.insert("maxPreferences".into(), json!(self.max_preferences));
        put_some(&mut request, "query", self.query.as_deref());
        if let Some(vector) = &self.query_vector {
            if vector.iter().any(|value| !value.is_finite()) {
                return Err(Error::Request(
                    "queryVector holds a value that is not a finite number".into(),
                ));
            }
            request.insert("queryVector".into(), json!(vector));
        }
        put_some(&mut request, "owner", self.owner.as_deref());
        if let Some(min_score) = self.min_score {
            if !min_score.is_finite() {
                return Err(Error::Request("minScore is not a finite number".into()));
            }
            request.insert("minScore".into(), json!(min_score));
        }
        put_some(&mut request, "requestId", self.request_id.as_deref());
        for (key, value) in [
            ("maxMessages", self.max_messages),
            ("maxEntities", self.max_entities),
            ("maxRelationships", self.max_relationships),
            ("maxConnectionFacts", self.max_connection_facts),
        ] {
            if value != 0 {
                request.insert(key.into(), json!(value));
            }
        }
        if self.time_windows {
            request.insert("timeWindows".into(), json!(true));
        }
        Ok(Value::Object(request))
    }

    fn timeout_ms(&self) -> u32 {
        self.timeout_ms
    }
}

/// Who asked for a remember.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum Origin {
    /// The owner asked ("remember that ...").
    Explicit,
    /// The host admitted the words without being asked (the `learning` profile only).
    Observed,
}

/// `memory_remember`: the owner's own words, through the durable journal (`explicit_write` or `learning`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Remember {
    /// The host's stable id for this spoken item and revision (up to 256 characters). The same id with the same words
    /// returns the existing receipt and runs nothing.
    pub source_id: String,
    /// The session.
    pub session_id: String,
    /// The words.
    pub text: String,
    /// When they were said, ISO 8601 with an offset.
    pub occurred_at: String,
    /// Who asked; the library's default is `explicit`.
    pub origin: Option<Origin>,
    /// The caller's correlation value.
    pub request_id: Option<String>,
    /// The job's time in the library (0: the context's default).
    pub timeout_ms: u32,
}

impl Remember {
    /// A remember of `text`, said at `occurred_at`.
    pub fn new(
        source_id: impl Into<String>,
        session_id: impl Into<String>,
        text: impl Into<String>,
        occurred_at: impl Into<String>,
    ) -> Remember {
        Remember {
            source_id: source_id.into(),
            session_id: session_id.into(),
            text: text.into(),
            occurred_at: occurred_at.into(),
            origin: None,
            request_id: None,
            timeout_ms: 0,
        }
    }
}

impl Operation for Remember {
    type Output = WriteResult;

    fn request(&self) -> Result<Value, Error> {
        let mut request = base("memory_remember");
        request.insert("sourceId".into(), json!(self.source_id));
        request.insert("sessionId".into(), json!(self.session_id));
        request.insert("text".into(), json!(self.text));
        request.insert("occurredAt".into(), json!(self.occurred_at));
        if let Some(origin) = self.origin {
            let origin = match origin {
                Origin::Explicit => "explicit",
                Origin::Observed => "observed",
            };
            request.insert("origin".into(), json!(origin));
        }
        put_some(&mut request, "requestId", self.request_id.as_deref());
        Ok(Value::Object(request))
    }

    fn timeout_ms(&self) -> u32 {
        self.timeout_ms
    }
}

/// `memory_operation_get`: the receipt of one operation, by its operation id or its source id.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct OperationGet {
    /// The operation's id.
    pub operation_id: Option<String>,
    /// The host's source id.
    pub source_id: Option<String>,
    /// The caller's correlation value.
    pub request_id: Option<String>,
    /// The job's time in the library (0: the context's default).
    pub timeout_ms: u32,
}

impl Operation for OperationGet {
    type Output = WriteResult;

    fn request(&self) -> Result<Value, Error> {
        let mut request = base("memory_operation_get");
        put_some(&mut request, "operationId", self.operation_id.as_deref());
        put_some(&mut request, "sourceId", self.source_id.as_deref());
        put_some(&mut request, "requestId", self.request_id.as_deref());
        Ok(Value::Object(request))
    }

    fn timeout_ms(&self) -> u32 {
        self.timeout_ms
    }
}

/// `memory_operation_resolve`: ends a partial or unknown write by keeping what it wrote.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OperationResolve {
    /// The operation's id.
    pub operation_id: String,
    /// The caller's correlation value.
    pub request_id: Option<String>,
    /// The job's time in the library (0: the context's default).
    pub timeout_ms: u32,
}

impl Operation for OperationResolve {
    type Output = WriteResult;

    fn request(&self) -> Result<Value, Error> {
        let mut request = base("memory_operation_resolve");
        request.insert("operationId".into(), json!(self.operation_id));
        request.insert("resolution".into(), json!("keep"));
        put_some(&mut request, "requestId", self.request_id.as_deref());
        Ok(Value::Object(request))
    }

    fn timeout_ms(&self) -> u32 {
        self.timeout_ms
    }
}

/// `memory_forget`: stops using what one remember produced, or the owner's memories by id. They are closed (kept as
/// history), not deleted.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Forget {
    /// A remember's operation id.
    pub operation_id: Option<String>,
    /// 1-50 memory ids.
    pub memory_ids: Option<Vec<String>>,
    /// The caller's correlation value.
    pub request_id: Option<String>,
    /// The job's time in the library (0: the context's default).
    pub timeout_ms: u32,
}

impl Forget {
    /// Forgets what one remember produced.
    pub fn operation(operation_id: impl Into<String>) -> Forget {
        Forget {
            operation_id: Some(operation_id.into()),
            ..Forget::default()
        }
    }

    /// Forgets the owner's memories by id.
    pub fn memories<I: IntoIterator<Item = S>, S: Into<String>>(ids: I) -> Forget {
        Forget {
            memory_ids: Some(ids.into_iter().map(Into::into).collect()),
            ..Forget::default()
        }
    }
}

impl Operation for Forget {
    type Output = WriteResult;

    fn request(&self) -> Result<Value, Error> {
        let mut request = base("memory_forget");
        put_some(&mut request, "operationId", self.operation_id.as_deref());
        if let Some(ids) = &self.memory_ids {
            request.insert("memoryIds".into(), json!(ids));
        }
        put_some(&mut request, "requestId", self.request_id.as_deref());
        Ok(Value::Object(request))
    }

    fn timeout_ms(&self) -> u32 {
        self.timeout_ms
    }
}

/// `memory_erase_owner`: deletes everything of the configured owner and purges their words from the journal. Cannot
/// be undone.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EraseOwner {
    /// Must be true for the library to run it.
    pub confirm: bool,
    /// The caller's correlation value.
    pub request_id: Option<String>,
    /// The job's time in the library (0: the context's default).
    pub timeout_ms: u32,
}

impl Operation for EraseOwner {
    type Output = WriteResult;

    fn request(&self) -> Result<Value, Error> {
        let mut request = base("memory_erase_owner");
        request.insert("confirm".into(), json!(self.confirm));
        put_some(&mut request, "requestId", self.request_id.as_deref());
        Ok(Value::Object(request))
    }

    fn timeout_ms(&self) -> u32 {
        self.timeout_ms
    }
}

/// `schema_status` (profile `admin`): what the store holds of AgentMemory's schema. Changes nothing.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct SchemaStatus {
    /// The job's time in the library (0: the context's default).
    pub timeout_ms: u32,
}

impl Operation for SchemaStatus {
    type Output = Outcome;

    fn request(&self) -> Result<Value, Error> {
        Ok(Value::Object(base("schema_status")))
    }

    fn timeout_ms(&self) -> u32 {
        self.timeout_ms
    }
}

/// `schema_bootstrap` (profile `admin`): creates what is missing of the schema and applies its migrations. A first
/// bootstrap takes seconds, so its job time defaults to 120 s.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SchemaBootstrap {
    /// The job's time in the library.
    pub timeout_ms: u32,
}

impl Default for SchemaBootstrap {
    fn default() -> SchemaBootstrap {
        SchemaBootstrap { timeout_ms: 120_000 }
    }
}

impl Operation for SchemaBootstrap {
    type Output = Outcome;

    fn request(&self) -> Result<Value, Error> {
        Ok(Value::Object(base("schema_bootstrap")))
    }

    fn timeout_ms(&self) -> u32 {
        self.timeout_ms
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_plain_recall_sends_only_what_an_older_library_reads() {
        let request = GetContext::vector("s", vec![1.0, 0.5]).request().unwrap();
        assert_eq!(
            request,
            json!({"schemaVersion": 1, "operation": "memory_get_context", "sessionId": "s", "maxFacts": 5,
                   "maxPreferences": 5, "queryVector": [1.0, 0.5]})
        );
    }

    #[test]
    fn sections_are_sent_when_asked_for() {
        let request = GetContext {
            max_messages: 3,
            time_windows: true,
            max_connection_facts: 8,
            min_score: Some(0.75),
            request_id: Some("r-1".into()),
            owner: Some("user-42".into()),
            ..GetContext::query("s", "where does my sister live?")
        }
        .request()
        .unwrap();
        assert_eq!(request["query"], "where does my sister live?");
        assert_eq!(request["maxMessages"], 3);
        assert_eq!(request["timeWindows"], true);
        assert_eq!(request["maxConnectionFacts"], 8);
        assert_eq!(request["minScore"], 0.75);
        assert_eq!(request["owner"], "user-42");
        assert!(request.get("maxEntities").is_none());
        assert!(request.get("queryVector").is_none());
    }

    #[test]
    fn a_vector_that_is_not_finite_is_refused_before_it_is_sent() {
        let error = GetContext::vector("s", vec![f32::NAN]).request().unwrap_err();
        assert!(matches!(error, Error::Request(_)));
    }

    #[test]
    fn writes_build_the_contract_documents() {
        let remember = Remember {
            origin: Some(Origin::Observed),
            ..Remember::new("src/1", "s", "I prefer tea.", "2026-11-20T10:00:00Z")
        };
        assert_eq!(
            remember.request().unwrap(),
            json!({"schemaVersion": 1, "operation": "memory_remember", "sourceId": "src/1", "sessionId": "s",
                   "text": "I prefer tea.", "occurredAt": "2026-11-20T10:00:00Z", "origin": "observed"})
        );
        assert_eq!(
            Forget::memories(["f-1", "p-2"]).request().unwrap()["memoryIds"],
            json!(["f-1", "p-2"])
        );
        assert_eq!(Forget::operation("op-1").request().unwrap()["operationId"], "op-1");
        let resolve = OperationResolve {
            operation_id: "op-1".into(),
            request_id: None,
            timeout_ms: 0,
        };
        assert_eq!(resolve.request().unwrap()["resolution"], "keep");
        let erase = EraseOwner {
            confirm: true,
            request_id: None,
            timeout_ms: 0,
        };
        assert_eq!(erase.request().unwrap()["confirm"], true);
        assert_eq!(SchemaBootstrap::default().timeout_ms(), 120_000);
        assert_eq!(
            CapabilitiesGet.request().unwrap(),
            json!({"schemaVersion": 1, "operation": "capabilities_get"})
        );
    }
}
