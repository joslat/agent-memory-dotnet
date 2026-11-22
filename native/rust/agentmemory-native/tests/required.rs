//! The Rust binding's own tests of native/gate/REQUIRED.md, section 1: what the shared cases cannot say.
//!
//! Tier A (the default) needs only the library: AGENTMEMORY_NATIVE_LIBRARY, or the file next to the test executable.
//! GATE_TIER=B with AMP_URI, AMP_USER and AMP_PASSWORD recalls from the gate's seeded store instead of asking for the
//! capabilities. Run with `--all-features` for the async items (R-ASYNC, B7).
//!
//! R-PACKAGE is not here: this crate carries no library until NATIVE-RELEASE, so there is no manifest to check yet.
//! B15 and the raw half of R-FREE-NULL are in agentmemory-native-sys/tests/abi.rs: they pass null pointers, which this
//! crate's API never does.

use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use agentmemory_native::raw::Native;
use agentmemory_native::sys;
use agentmemory_native::{
    CallError, Context, Error, GetContext, LoadError, OperationStatus, Polled, DEFAULT_CLOSE_WAIT,
};
use serde_json::{json, Value};

const NEAR_AXIS_0: [f32; 8] = [1.0, 0.1, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0];
const A_IDS: [&str; 3] = ["gate-fact-a-colour", "gate-fact-a-work", "gate-pref-a-concise"];

/// One context per store per process: the tests that open one take turns.
static STORE: Mutex<()> = Mutex::new(());

fn turn() -> MutexGuard<'static, ()> {
    STORE.lock().unwrap_or_else(PoisonError::into_inner)
}

fn with_store() -> bool {
    std::env::var("GATE_TIER").is_ok_and(|tier| !tier.eq_ignore_ascii_case("A"))
}

fn config(limits: Option<Value>) -> Value {
    let store = with_store();
    let variable = |name: &str, otherwise: &str| std::env::var(name).unwrap_or_else(|_| otherwise.to_owned());
    let mut config = json!({
        "schemaVersion": 1, "profile": "read_only", "owner": {"mode": "fixed", "id": "gate-owner-a"},
        "store": {
            "uri": if store { variable("AMP_URI", "bolt://127.0.0.1:7687") } else { "bolt://127.0.0.1:9".into() },
            "username": variable("AMP_USER", "neo4j"),
            "password": variable("AMP_PASSWORD", "unused"),
            "verifyConnectivity": store,
        },
        "embedding": {"dimensions": 8, "model": "gate-unit-axes"},
    });
    if let Some(limits) = limits {
        config["limits"] = limits;
    }
    config
}

fn capabilities() -> Value {
    json!({"schemaVersion": 1, "operation": "capabilities_get"})
}

fn recall(session: &str) -> GetContext {
    GetContext {
        min_score: Some(0.0),
        ..GetContext::vector(session, NEAR_AXIS_0.to_vec())
    }
}

/// With a store, a recall of owner A's memories; without one, the capabilities, which need no store.
fn checked_call(memory: &Context, session: &str) {
    if with_store() {
        let result = memory.get_context(&recall(session)).unwrap();
        assert_eq!(result.status, OperationStatus::Ok, "{:?}", result.error);
        assert_eq!(recalled_ids(&result.document), A_IDS);
    } else {
        let outcome = memory.call(&capabilities(), 0).unwrap();
        assert!(outcome.is_ok(), "{}", outcome.document);
    }
}

fn recalled_ids(document: &Value) -> Vec<String> {
    let mut ids: Vec<String> = ["facts", "preferences"]
        .iter()
        .flat_map(|key| document[*key].as_array().cloned().unwrap_or_default())
        .filter_map(|item| item["id"].as_str().map(str::to_owned))
        .collect();
    ids.sort();
    ids.dedup();
    ids
}

fn scratch(name: &str) -> PathBuf {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_nanos());
    let folder = std::env::temp_dir().join(format!("am-rust-{name}-{}-{nanos}", std::process::id()));
    std::fs::create_dir_all(&folder).unwrap();
    folder
}

// The loader ----------------------------------------------------------------------------------------------------

#[test]
fn r_load_path_an_explicit_path_or_the_variable_and_nowhere_else() {
    let (path, source) = sys::default_path().unwrap();
    match std::env::var_os(sys::LIBRARY_ENV).filter(|value| !value.is_empty()) {
        Some(value) => {
            assert_eq!(source, sys::Source::Environment);
            assert_eq!(path, PathBuf::from(&value));
            let native = Native::load().unwrap();
            // The same file (on Windows the loader gets the plain form of the verbatim canonical path).
            assert_eq!(
                std::fs::canonicalize(native.path()).unwrap(),
                std::fs::canonicalize(&value).unwrap()
            );
        }
        None => {
            assert_eq!(source, sys::Source::NextToExecutable);
            let executable = std::env::current_exe().unwrap();
            assert_eq!(path, executable.parent().unwrap().join(sys::LIBRARY_FILE_NAME));
        }
    }
    let missing = scratch("missing").join("nowhere").join(sys::LIBRARY_FILE_NAME);
    let error = Native::load_from(&missing).unwrap_err();
    assert!(matches!(error, LoadError::NotFound { .. }), "{error}");
}

#[test]
fn r_load_unloadable_a_file_that_cannot_be_loaded_is_refused_with_an_actionable_error() {
    let folder = scratch("unloadable");
    let fake = folder.join(sys::LIBRARY_FILE_NAME);
    std::fs::write(&fake, b"\x7fELF but not really a library").unwrap();
    let error = Native::load_from(&fake).unwrap_err();
    let text = error.to_string();
    assert!(matches!(error, LoadError::Unloadable { .. }), "{text}");
    assert!(text.contains("agentmemory_native"), "{text}");
    assert!(text.contains(std::env::consts::ARCH), "{text}");
    assert!(text.contains("built for this platform"), "{text}");
    let _ = std::fs::remove_dir_all(folder);
}

#[test]
fn r_load_foreign_a_loadable_library_without_the_exports_is_refused() {
    let other: Option<PathBuf> = if cfg!(windows) {
        let root = std::env::var_os("SystemRoot").unwrap_or_else(|| "C:\\Windows".into());
        Some(Path::new(&root).join("System32").join("kernel32.dll"))
    } else if cfg!(target_os = "macos") {
        None // macOS keeps its system libraries in the dyld cache, not as files
    } else {
        [
            "/lib/x86_64-linux-gnu/libm.so.6",
            "/lib/aarch64-linux-gnu/libm.so.6",
            "/usr/lib64/libm.so.6",
            "/usr/lib/x86_64-linux-gnu/libm.so.6",
            "/usr/lib/aarch64-linux-gnu/libm.so.6",
        ]
        .iter()
        .map(PathBuf::from)
        .find(|path| path.is_file())
    };
    let Some(other) = other else {
        eprintln!("R-LOAD-FOREIGN: no system library file to try on this platform");
        return;
    };
    let error = Native::load_from(&other).unwrap_err();
    assert!(matches!(error, LoadError::NotAgentMemory { .. }), "{error}");
    assert!(
        error.to_string().contains("not an AgentMemory native library"),
        "{error}"
    );
}

#[test]
fn r_no_dotnet_the_target_image_has_no_dotnet() {
    // Only where the run says it is the target image (the Docker recipe sets it): CI runners have .NET installed.
    if std::env::var("AGENTMEMORY_TARGET_IMAGE").as_deref() != Ok("1") {
        eprintln!("R-NO-DOTNET: not the target image (AGENTMEMORY_TARGET_IMAGE=1 is not set)");
        return;
    }
    let path = std::env::var_os("PATH").unwrap_or_default();
    let found: Vec<PathBuf> = std::env::split_paths(&path)
        .flat_map(|folder| [folder.join("dotnet"), folder.join("dotnet.exe")])
        .filter(|candidate| candidate.is_file())
        .collect();
    assert!(found.is_empty(), "the target image must not have .NET: {found:?}");
    assert!(!Path::new("/usr/share/dotnet").exists() && !Path::new("/usr/lib/dotnet").exists());
}

// Handles, contexts and the waiter ----------------------------------------------------------------------------

#[test]
fn r_handles_context_and_job_handles_are_unsigned_64_bit() {
    let _turn = turn();
    let memory = Context::open(&config(None)).unwrap();
    let context: u64 = memory.handle();
    assert!(context > 0);
    let job = memory.submit(&capabilities(), 0).unwrap();
    let handle: u64 = job.handle();
    assert!(handle > 0);
    // The same values, through the raw ABI.
    let mut output = memory.native().poll(context, handle, 0);
    for _ in 0..200 {
        if output.disposition == sys::AM_V1_TERMINAL {
            break;
        }
        output = memory.native().poll(context, handle, 50);
    }
    assert_eq!(output.status, sys::AM_V1_OK);
    assert_eq!(output.disposition, sys::AM_V1_TERMINAL);
    assert!(job.wait(Duration::from_secs(10)).unwrap().is_ok());
    job.release().unwrap();
    println!(
        "METRIC context_handle_bits={} job_handle_bits={}",
        64 - context.leading_zeros(),
        64 - handle.leading_zeros()
    );
    memory.close(DEFAULT_CLOSE_WAIT).unwrap();
}

#[test]
fn r_waiter_close_a_thread_blocked_in_am_v1_wait_is_woken_by_close() {
    let _turn = turn();
    let memory = Context::open(&config(None)).unwrap();
    std::thread::scope(|scope| {
        let waiter = scope.spawn(|| {
            let started = Instant::now();
            let found = memory.wait_any(20_000);
            (found, started.elapsed())
        });
        std::thread::sleep(Duration::from_millis(300));
        assert!(!waiter.is_finished(), "the thread is blocked in am_v1_wait");
        memory.close(DEFAULT_CLOSE_WAIT).unwrap();
        let (found, elapsed) = waiter.join().unwrap();
        match found {
            Ok(job) => assert_eq!(job, None, "woken with no job"),
            Err(error) => assert_eq!(error.call_error(), Some(CallError::InvalidHandle), "{error}"),
        }
        assert!(elapsed < Duration::from_secs(5), "woken after {elapsed:?}");
    });
}

#[test]
fn a_job_waited_for_through_the_waiter_returns_copies_and_releases_once() {
    let _turn = turn();
    let memory = Context::open(&config(None)).unwrap();
    let job = memory.submit(&capabilities(), 0).unwrap();
    let first = job.wait(Duration::from_secs(10)).unwrap();
    let Polled::Terminal(second) = job.poll(0).unwrap() else {
        panic!("a finished job stays terminal");
    };
    assert_eq!(first, second, "repeated terminal polls return copies");
    job.cancel().unwrap(); // a finished job: nothing to stop, and not an error
    job.release().unwrap();
    let state = memory.state().unwrap();
    // read_only: recall allowed, writes quiescent (there are none); ADMISSION_ALLOWED is the write profiles' flag.
    assert!(
        state.read_allowed() && state.write_quiescent() && !state.closing(),
        "{state:?}"
    );
    memory.close(DEFAULT_CLOSE_WAIT).unwrap();
}

#[test]
fn b8_a_refused_second_open_is_reported_and_the_store_is_free_after_close() {
    // Opening is synchronous in this binding: there is no cancelled open to leak. What B8 protects is that a context
    // never keeps the store: one context per store, until it closes.
    let _turn = turn();
    let first = Context::open(&config(None)).unwrap();
    match Context::open(&config(None)) {
        Err(Error::Open { error, code, .. }) => {
            assert_eq!(error, CallError::Busy);
            assert_eq!(code, "authority_held");
        }
        other => panic!("expected BUSY authority_held, got {other:?}"),
    }
    first.close(DEFAULT_CLOSE_WAIT).unwrap();
    Context::open(&config(None)).unwrap().close(DEFAULT_CLOSE_WAIT).unwrap();
}

#[test]
fn a_bad_configuration_is_an_open_error_with_the_library_reason() {
    let mut bad = config(None);
    bad["store"]["surprise"] = json!(1);
    match Context::open(&bad) {
        Err(Error::Open { error, code, message }) => {
            assert_eq!(error, CallError::InvalidArgument);
            assert_eq!(code, "unknown_property");
            assert!(!message.is_empty());
        }
        other => panic!("expected INVALID_ARGUMENT unknown_property, got {other:?}"),
    }
}

#[test]
fn an_operation_that_fails_is_a_result_with_its_status_not_an_error() {
    let _turn = turn();
    let memory = Context::open(&config(None)).unwrap();
    let wrong = GetContext::vector("s", vec![1.0, 2.0, 3.0]);
    let result = memory.get_context(&wrong).unwrap();
    assert_eq!(result.status, OperationStatus::Invalid);
    assert_eq!(result.error.unwrap().code, "dimension_mismatch");
    let capabilities = memory.capabilities().unwrap();
    assert!(capabilities.offers("memory_get_context"));
    assert!(capabilities.has_feature("am_v1_wait"));
    memory.close(DEFAULT_CLOSE_WAIT).unwrap();
}

#[test]
fn r_memory_500_calls_do_not_grow_the_process_by_64_mib() {
    let _turn = turn();
    let memory = Context::open(&config(None)).unwrap();
    for i in 0..50 {
        checked_call(&memory, &format!("warm{i}"));
    }
    let Some(before) = rss_kib() else {
        eprintln!("R-MEMORY: no resident-memory reading on this platform");
        memory.close(DEFAULT_CLOSE_WAIT).unwrap();
        return;
    };
    let mut latencies = Vec::with_capacity(500);
    for i in 0..500 {
        let started = Instant::now();
        checked_call(&memory, &format!("m{}", i % 7));
        latencies.push(started.elapsed());
    }
    let growth = rss_kib().unwrap().saturating_sub(before);
    latencies.sort();
    println!(
        "METRIC {}_p50_ms={:.2} {}_p95_ms={:.2} rss_growth_kib={growth}",
        if with_store() { "recall" } else { "capabilities" },
        latencies[250].as_secs_f64() * 1000.0,
        if with_store() { "recall" } else { "capabilities" },
        latencies[474].as_secs_f64() * 1000.0,
    );
    assert!(growth < 64 * 1024, "grew by {growth} KiB");
    memory.close(DEFAULT_CLOSE_WAIT).unwrap();
}

/// This process's resident memory in KiB, where it can be read without unsafe code.
fn rss_kib() -> Option<u64> {
    if cfg!(target_os = "linux") {
        let status = std::fs::read_to_string("/proc/self/status").ok()?;
        let line = status.lines().find(|line| line.starts_with("VmRSS:"))?;
        line.split_whitespace().nth(1)?.parse().ok()
    } else if cfg!(target_os = "macos") {
        let output = std::process::Command::new("ps")
            .args(["-o", "rss=", "-p", &std::process::id().to_string()])
            .output()
            .ok()?;
        String::from_utf8_lossy(&output.stdout).trim().parse().ok()
    } else {
        None
    }
}

// Async (feature "async") ------------------------------------------------------------------------------------------

#[cfg(feature = "async")]
mod asynchronous {
    use super::*;
    use std::future::Future;
    use std::pin::Pin;
    use std::sync::Arc;
    use std::task::{Context as TaskContext, Poll, Wake, Waker};
    use std::thread::Thread;

    struct Unpark(Thread);

    impl Wake for Unpark {
        fn wake(self: Arc<Self>) {
            self.0.unpark();
        }
    }

    struct Ignore;

    impl Wake for Ignore {
        fn wake(self: Arc<Self>) {}
    }

    /// A minimal executor: polls the futures on this thread, parking between wake-ups.
    fn join_all<F: Future + Unpin>(futures: Vec<F>) -> Vec<F::Output> {
        let waker = Waker::from(Arc::new(Unpark(std::thread::current())));
        let mut context = TaskContext::from_waker(&waker);
        let mut pending: Vec<Option<F>> = futures.into_iter().map(Some).collect();
        let mut results: Vec<Option<F::Output>> = pending.iter().map(|_| None).collect();
        while results.iter().any(Option::is_none) {
            for (slot, result) in pending.iter_mut().zip(results.iter_mut()) {
                if let Some(future) = slot {
                    if let Poll::Ready(output) = Pin::new(future).poll(&mut context) {
                        *result = Some(output);
                        *slot = None;
                    }
                }
            }
            if results.iter().any(Option::is_none) {
                std::thread::park_timeout(Duration::from_millis(100));
            }
        }
        results.into_iter().map(Option::unwrap).collect()
    }

    fn block_on<F: Future + Unpin>(future: F) -> F::Output {
        join_all(vec![future]).pop().unwrap()
    }

    fn async_call(memory: &Context, session: &str) {
        if with_store() {
            let result = block_on(memory.run_async(&recall(session))).unwrap();
            assert_eq!(recalled_ids(&result.document), A_IDS);
        } else {
            let outcome = block_on(memory.call_async(&capabilities(), 0)).unwrap();
            assert!(outcome.is_ok(), "{}", outcome.document);
        }
    }

    #[test]
    fn r_async_one_call_sixteen_at_once_and_a_dropped_future_cancels() {
        let _turn = turn();
        let memory = Context::open(&config(None)).unwrap();
        async_call(&memory, "s");

        if with_store() {
            let futures: Vec<_> = (0..16).map(|i| memory.run_async(&recall(&format!("s{i}")))).collect();
            for result in join_all(futures) {
                assert_eq!(recalled_ids(&result.unwrap().document), A_IDS);
            }
        } else {
            let futures: Vec<_> = (0..16).map(|_| memory.call_async(&capabilities(), 0)).collect();
            for result in join_all(futures) {
                assert!(result.unwrap().is_ok());
            }
        }

        // Rust cancels a future by dropping it: the job is cancelled in the library and released, and an awaited job
        // reports its cancellation as a result.
        let job = memory.submit(&recall("c").request_document(), 5000).unwrap();
        job.cancel().unwrap();
        let outcome = block_on(std::future::IntoFuture::into_future(job)).unwrap();
        assert!(
            matches!(
                outcome.status,
                OperationStatus::Cancelled
                    | OperationStatus::Ok
                    | OperationStatus::Unavailable
                    | OperationStatus::Timeout
            ),
            "{}",
            outcome.document
        );
        let mut dropped = memory.run_async(&recall("c"));
        let waker = Waker::from(Arc::new(Ignore));
        let _ = Pin::new(&mut dropped).poll(&mut TaskContext::from_waker(&waker));
        drop(dropped);
        async_call(&memory, "after");
        memory.close(DEFAULT_CLOSE_WAIT).unwrap();
    }

    #[test]
    fn b7_300_futures_dropped_at_any_point_leak_no_job() {
        // Unreleased jobs count against maxPendingJobs + 256: 300 leaked handles would refuse every submit after.
        let _turn = turn();
        let memory = Context::open(&config(Some(json!({"maxRunningJobs": 1, "maxPendingJobs": 1})))).unwrap();
        let waker = Waker::from(Arc::new(Ignore));
        for i in 0..300 {
            let mut future = if with_store() {
                memory.call_async(&recall("c").request_document(), 5000)
            } else {
                memory.call_async(&capabilities(), 0)
            };
            for _ in 0..i % 4 {
                match Pin::new(&mut future).poll(&mut TaskContext::from_waker(&waker)) {
                    Poll::Ready(Err(error)) => {
                        assert_eq!(
                            error.call_error(),
                            Some(CallError::Overloaded),
                            "only backpressure may refuse a submit here: {error}"
                        );
                        break;
                    }
                    Poll::Ready(Ok(_)) => break,
                    Poll::Pending => std::thread::yield_now(),
                }
            }
            drop(future);
        }
        for _ in 0..100 {
            let attempt = if with_store() {
                block_on(memory.run_async(&recall("after"))).map(|result| recalled_ids(&result.document))
            } else {
                block_on(memory.call_async(&capabilities(), 0)).map(|_| Vec::new())
            };
            match attempt {
                Ok(ids) => {
                    if with_store() {
                        assert_eq!(ids, A_IDS);
                    }
                    memory.close(Duration::from_secs(30)).unwrap();
                    return;
                }
                Err(error) => {
                    assert_eq!(error.call_error(), Some(CallError::Overloaded), "{error}");
                    std::thread::sleep(Duration::from_millis(100));
                }
            }
        }
        panic!("capacity never came back: jobs leaked");
    }

    /// The request document of an operation, for the generic call.
    trait Document {
        fn request_document(&self) -> Value;
    }

    impl Document for GetContext {
        fn request_document(&self) -> Value {
            agentmemory_native::Operation::request(self).unwrap()
        }
    }
}
