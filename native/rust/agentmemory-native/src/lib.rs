//! The AgentMemory native library from Rust: owner-scoped recall and journaled writes against Neo4j over the library's
//! C ABI (`native/include/agentmemory_native.h`, `native/CONTRACT.md`), with no .NET on the machine.
//!
//! ```no_run
//! use agentmemory_native::{Context, GetContext};
//! use serde_json::json;
//!
//! let memory = Context::open(&json!({
//!     "schemaVersion": 1,
//!     "profile": "read_only",
//!     "owner": {"mode": "fixed", "id": "user-42"},
//!     "store": {"uri": "bolt://localhost:7687", "username": "neo4j", "password": "..."},
//!     "embedding": {"dimensions": 8, "model": "my-model"}
//! }))?;
//! let recall = memory.get_context(&GetContext::vector("session-1", vec![1.0, 0.1, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]))?;
//! if recall.is_ok() {
//!     for fact in &recall.facts {
//!         println!("{} {} {}", fact.subject, fact.predicate, fact.object);
//!     }
//! }
//! # Ok::<(), agentmemory_native::Error>(())
//! ```
//!
//! Three status layers stay apart, as the header asks:
//!
//! 1. A call's return code: [`Error::Call`] with a [`CallError`] (`Overloaded`, `Closing`, `InvalidHandle`, ...).
//! 2. A poll's disposition: [`Polled::Pending`] or [`Polled::Terminal`].
//! 3. The operation's own status inside the result: [`OperationStatus`] (`Ok`, `Timeout`, `Unavailable`, ...). A
//!    recall that found the store unreachable is a [`Recall`] with that status, not an error.
//!
//! The library is loaded at run time from `AGENTMEMORY_NATIVE_LIBRARY`, else from its platform file name next to the
//! executable, and never unloaded. [`raw`] maps the ABI one to one for what the [`Context`] API does not cover.
//!
//! With the `async` feature, [`Context::call_async`] and [`Context::run_async`] return futures that any executor can
//! run, and a [`Job`] can be awaited. Dropping such a future cancels its job.
#![deny(unsafe_code)]
#![warn(missing_docs)]

mod context;
mod error;
#[cfg(feature = "async")]
mod future;
pub mod raw;
mod requests;
mod results;

pub use context::{Context, Job, DEFAULT_CLOSE_WAIT};
pub use error::{CallError, Error, LoadError};
#[cfg(feature = "async")]
pub use future::{JobFuture, OperationFuture};
pub use requests::{
    CapabilitiesGet, EraseOwner, Forget, GetContext, Operation, OperationGet, OperationResolve, Origin, Remember,
    Request, SchemaBootstrap, SchemaStatus,
};
pub use results::{
    AbiVersion, Capabilities, ContextState, Entity, Fact, FromOutcome, OperationError, OperationStatus, Outcome,
    PastMessage, Polled, Preference, Recall, Receipt, ReceiptState, Relationship, Timing, WriteResult,
};

/// The raw declarations crate, for what this crate does not wrap.
pub use agentmemory_native_sys as sys;
