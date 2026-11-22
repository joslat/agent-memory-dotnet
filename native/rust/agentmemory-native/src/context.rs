//! An owned context of the library, its jobs, and the one waiter thread per context that wakes whoever waits for a job.

use std::collections::HashMap;
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError};
use std::task::Waker;
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use agentmemory_native_sys as sys;
use serde::Serialize;
use serde_json::Value;

use crate::error::check;
use crate::raw::Native;
use crate::requests::{
    CapabilitiesGet, EraseOwner, Forget, GetContext, Operation, OperationGet, OperationResolve, Remember,
    SchemaBootstrap, SchemaStatus,
};
use crate::results::{Capabilities, ContextState, FromOutcome, Outcome, Polled, Recall, WriteResult};
use crate::{CallError, Error};

/// How long [`Drop`] waits for running work to settle before it gives up on closing.
pub const DEFAULT_CLOSE_WAIT: Duration = Duration::from_secs(10);
/// What [`Context::call`] waits beyond the job's own time, for the library's deadline to end the job first.
const GRACE: Duration = Duration::from_secs(5);
/// One turn of the waiter inside `am_v1_wait`. It returns at once when a job finishes or the context starts closing,
/// so this only bounds how long the waiter stays in the library after the last waited-for job ended.
const WAITER_TURN_MS: u32 = 250;
/// ABI 1.0 has no `am_v1_wait`: the waiter polls each waited-for job, then sleeps this long.
const POLL_FALLBACK: Duration = Duration::from_millis(1);
/// Between `am_v1_close` calls while the library answers BUSY.
const CLOSE_RETRY: Duration = Duration::from_millis(10);

/// A logical context of the library: open on [`Context::open`], closed on [`Context::close`] or when dropped.
///
/// `Context` is `Send` and `Sync`: share it by reference (or in an `Arc`) between threads. Operations block the
/// calling thread until their result arrives; one waiter thread per context, started at the first wait, waits inside
/// the library for finished jobs (`am_v1_wait`; on an ABI 1.0 library it polls) and wakes the threads and futures
/// waiting for them.
#[derive(Debug)]
pub struct Context {
    shared: Arc<Shared>,
}

/// One submitted operation. Dropping it releases its handle; a released handle is never reused.
#[derive(Debug)]
pub struct Job {
    shared: Arc<Shared>,
    handle: u64,
    released: bool,
}

#[derive(Debug)]
pub(crate) struct Shared {
    native: Native,
    handle: u64,
    default_timeout_ms: u32,
    use_wait: bool,
    tracking: Mutex<Tracking>,
    changed: Condvar,
}

#[derive(Debug, Default)]
struct Tracking {
    /// Every unreleased job submitted through this context.
    jobs: HashMap<u64, Tracked>,
    /// Threads and futures now waiting for a job's result through the waiter.
    waiting: usize,
    /// close has begun: the waiter stops, and waits end with Error::Closed when no result is there.
    closing: bool,
    /// am_v1_close returned OK (or the handle was already gone).
    closed: bool,
    waiter: Option<JoinHandle<()>>,
    /// am_v1_wait failed with this code; the waiter has stopped.
    waiter_failed: Option<CallError>,
}

#[derive(Debug, Default)]
struct Tracked {
    finished: bool,
    waker: Option<Waker>,
}

/// What a wait for one job found.
pub(crate) enum Readiness {
    /// The library reported the job finished (or the waiter cannot tell): poll it.
    Finished,
    /// Still running; a waker is registered.
    Waiting,
    /// The context is closing.
    Closing,
    /// The waiter failed.
    Failed(CallError),
}

impl Shared {
    fn lock(&self) -> MutexGuard<'_, Tracking> {
        self.tracking.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Starts the waiter thread when none runs. Called with the lock held.
    fn ensure_waiter(self: &Arc<Shared>, tracking: &mut Tracking) {
        if tracking.waiter.is_some() || tracking.closing || tracking.waiter_failed.is_some() {
            return;
        }
        let shared = Arc::clone(self);
        match thread::Builder::new()
            .name("agentmemory-waiter".into())
            .spawn(move || shared.run_waiter())
        {
            Ok(handle) => tracking.waiter = Some(handle),
            Err(_) => tracking.waiter_failed = Some(CallError::InternalError),
        }
    }

    fn run_waiter(&self) {
        loop {
            let polled: Vec<u64> = {
                let mut tracking = self.lock();
                while tracking.waiting == 0 && !tracking.closing {
                    tracking = self.changed.wait(tracking).unwrap_or_else(PoisonError::into_inner);
                }
                if tracking.closing {
                    return;
                }
                if self.use_wait {
                    Vec::new()
                } else {
                    tracking
                        .jobs
                        .iter()
                        .filter(|(_, tracked)| !tracked.finished)
                        .map(|(handle, _)| *handle)
                        .collect()
                }
            };
            if self.use_wait {
                match self.native.wait(self.handle, WAITER_TURN_MS) {
                    Some((sys::AM_V1_OK, 0)) => {}
                    Some((sys::AM_V1_OK, job)) => self.finished(job),
                    Some((status, _)) => return self.fail(status),
                    None => return self.fail(sys::AM_V1_UNSUPPORTED_ABI),
                }
            } else {
                for job in polled {
                    let output = self.native.poll(self.handle, job, 0);
                    // The result buffer is freed here; the job's owner takes its own copy with a poll.
                    if output.status != sys::AM_V1_OK || output.disposition == sys::AM_V1_TERMINAL {
                        self.finished(job);
                    }
                }
                thread::sleep(POLL_FALLBACK);
            }
        }
    }

    /// Marks a job finished and wakes whoever waits for it.
    fn finished(&self, job: u64) {
        let waker = {
            let mut tracking = self.lock();
            match tracking.jobs.get_mut(&job) {
                Some(tracked) => {
                    tracked.finished = true;
                    tracked.waker.take()
                }
                // Released already, or submitted through the raw API on this handle: nothing waits here.
                None => None,
            }
        };
        self.changed.notify_all();
        if let Some(waker) = waker {
            waker.wake();
        }
    }

    fn fail(&self, status: i32) {
        let wakers = {
            let mut tracking = self.lock();
            if !tracking.closing {
                tracking.waiter_failed = CallError::from_code(status).or(Some(CallError::InternalError));
            }
            Self::take_wakers(&mut tracking)
        };
        self.changed.notify_all();
        wakers.into_iter().for_each(Waker::wake);
    }

    fn take_wakers(tracking: &mut Tracking) -> Vec<Waker> {
        tracking
            .jobs
            .values_mut()
            .filter_map(|tracked| tracked.waker.take())
            .collect()
    }

    /// Registers `waker` for `job` (a future's wait). `registered` says whether this future already counts as waiting.
    #[cfg(feature = "async")]
    pub(crate) fn register(self: &Arc<Shared>, job: u64, waker: &Waker, registered: &mut bool) -> Readiness {
        let mut tracking = self.lock();
        if tracking.closing {
            return Readiness::Closing;
        }
        if let Some(code) = tracking.waiter_failed {
            return Readiness::Failed(code);
        }
        let tracked = tracking.jobs.entry(job).or_default();
        if tracked.finished {
            return Readiness::Finished;
        }
        tracked.waker = Some(waker.clone());
        if !*registered {
            *registered = true;
            tracking.waiting += 1;
        }
        self.ensure_waiter(&mut tracking);
        drop(tracking);
        self.changed.notify_all();
        Readiness::Waiting
    }

    /// A future stops waiting (it has its result, or it was dropped).
    #[cfg(feature = "async")]
    pub(crate) fn unregister(&self, job: u64, registered: &mut bool) {
        let mut tracking = self.lock();
        if let Some(tracked) = tracking.jobs.get_mut(&job) {
            tracked.waker = None;
        }
        if *registered {
            *registered = false;
            tracking.waiting -= 1;
        }
    }

    /// Blocks until the job is reported finished, the context starts closing, the waiter fails, or `deadline` passes.
    fn block_until_finished(self: &Arc<Shared>, job: u64, deadline: Instant) -> Readiness {
        let mut tracking = self.lock();
        tracking.waiting += 1;
        self.ensure_waiter(&mut tracking);
        self.changed.notify_all();
        let readiness = loop {
            if tracking.closing {
                break Readiness::Closing;
            }
            if let Some(code) = tracking.waiter_failed {
                break Readiness::Failed(code);
            }
            if tracking.jobs.get(&job).map_or(true, |tracked| tracked.finished) {
                break Readiness::Finished;
            }
            let now = Instant::now();
            if now >= deadline {
                break Readiness::Waiting;
            }
            tracking = self
                .changed
                .wait_timeout(tracking, deadline - now)
                .unwrap_or_else(PoisonError::into_inner)
                .0;
        };
        tracking.waiting -= 1;
        readiness
    }

    pub(crate) fn poll(&self, job: u64, wait_ms: u32) -> Result<Polled, Error> {
        let output = self.native.poll(self.handle, job, wait_ms);
        check("am_v1_poll", output.status)?;
        match (output.disposition, output.result) {
            (sys::AM_V1_TERMINAL, Some(bytes)) => Ok(Polled::Terminal(Outcome::from_slice(&bytes)?)),
            _ => Ok(Polled::Pending),
        }
    }
}

impl Context {
    /// Opens a context with the library from `AGENTMEMORY_NATIVE_LIBRARY` (else next to the executable).
    ///
    /// `config` is the configuration document of `native/CONTRACT.md`. Opening may connect to the store: call it
    /// from a thread that may block.
    pub fn open(config: &impl Serialize) -> Result<Context, Error> {
        Context::open_with(Native::load()?, config)
    }

    /// Opens a context with a library loaded by the caller.
    pub fn open_with(native: Native, config: &impl Serialize) -> Result<Context, Error> {
        Context::open_inner(native, config, native.has_wait())
    }

    pub(crate) fn open_inner(native: Native, config: &impl Serialize, use_wait: bool) -> Result<Context, Error> {
        let config = serde_json::to_value(config).map_err(|error| Error::Request(error.to_string()))?;
        // The library reads a JSON null as absent; so does this.
        let default_timeout_ms = config
            .pointer("/limits/defaultTimeoutMs")
            .and_then(Value::as_u64)
            .and_then(|value| u32::try_from(value).ok())
            .filter(|value| *value > 0)
            .unwrap_or(2000);
        let bytes = serde_json::to_vec(&config).map_err(|error| Error::Request(error.to_string()))?;
        let opened = native.open(sys::AM_V1_ABI_VERSION, &bytes);
        if let Some(error) = CallError::from_code(opened.status) {
            let detail = opened
                .error
                .and_then(|bytes| serde_json::from_slice::<Value>(&bytes).ok())
                .and_then(|document| document.get("error").cloned())
                .unwrap_or(Value::Null);
            let text = |key: &str| detail.get(key).and_then(Value::as_str).map(str::to_owned);
            return Err(Error::Open {
                error,
                code: text("code").unwrap_or_else(|| "unknown".into()),
                message: text("message").unwrap_or_default(),
            });
        }
        Ok(Context {
            shared: Arc::new(Shared {
                native,
                handle: opened.context,
                default_timeout_ms,
                use_wait,
                tracking: Mutex::new(Tracking::default()),
                changed: Condvar::new(),
            }),
        })
    }

    /// The context handle, an opaque unsigned 64-bit value.
    pub fn handle(&self) -> u64 {
        self.shared.handle
    }

    /// The library this context runs on.
    pub fn native(&self) -> Native {
        self.shared.native
    }

    /// The configuration's `limits.defaultTimeoutMs` (2000 when not set): the job time a request with 0 gets.
    pub fn default_timeout_ms(&self) -> u32 {
        self.shared.default_timeout_ms
    }

    // Low level, one to one onto the ABI ---------------------------------------------------------------------------

    /// Schedules one operation. `timeout_ms` bounds it in the library (0: the default). An error means the job was not
    /// admitted ([`CallError::Overloaded`], [`CallError::Closing`], ...); a request with wrong content is admitted and
    /// ends at once with status `invalid`.
    pub fn submit(&self, request: &impl Serialize, timeout_ms: u32) -> Result<Job, Error> {
        let bytes = serde_json::to_vec(request).map_err(|error| Error::Request(error.to_string()))?;
        self.submit_bytes(&bytes, timeout_ms)
    }

    /// Schedules one operation from the request's exact bytes.
    pub fn submit_bytes(&self, request: &[u8], timeout_ms: u32) -> Result<Job, Error> {
        let (status, handle) = self.shared.native.submit(self.shared.handle, request, timeout_ms);
        check("am_v1_submit", status)?;
        self.shared.lock().jobs.insert(handle, Tracked::default());
        Ok(Job {
            shared: Arc::clone(&self.shared),
            handle,
            released: false,
        })
    }

    /// One coherent snapshot of the context's revisions and flags. No I/O.
    pub fn state(&self) -> Result<ContextState, Error> {
        let output = self.shared.native.state(self.shared.handle);
        check("am_v1_state", output.status)?;
        Ok(ContextState {
            state_revision: output.state_revision,
            policy_epoch: output.policy_epoch,
            deletion_epoch: output.deletion_epoch,
            memory_revision: output.memory_revision,
            flags: output.flags,
        })
    }

    /// ABI 1.1: a finished job that no wait has reported yet, waiting up to `wait_ms`; `None` when none finished in
    /// time or the context is closing. Each job is reported once, to whichever waiter asks first, so do not mix this
    /// with [`Job::wait`], [`Context::call`] or futures on the same context: their waiter would take reports too.
    pub fn wait_any(&self, wait_ms: u32) -> Result<Option<u64>, Error> {
        match self.shared.native.wait(self.shared.handle, wait_ms) {
            None => Err(Error::Call {
                function: "am_v1_wait",
                error: CallError::UnsupportedAbi,
            }),
            Some((status, job)) => {
                check("am_v1_wait", status)?;
                Ok((job != 0).then_some(job))
            }
        }
    }

    // Operations, blocking ------------------------------------------------------------------------------------------

    /// Submits, waits for the result, and releases the job. Waits the job's time plus 5 s; then it cancels the job
    /// and waits 5 s more before it gives up with [`Error::Timeout`].
    pub fn call(&self, request: &impl Serialize, timeout_ms: u32) -> Result<Outcome, Error> {
        let job = self.submit(request, timeout_ms)?;
        let own_time = if timeout_ms == 0 {
            self.shared.default_timeout_ms
        } else {
            timeout_ms
        };
        match job.wait(Duration::from_millis(u64::from(own_time)) + GRACE) {
            Err(Error::Timeout { .. }) => {
                job.cancel()?;
                job.wait(GRACE)
            }
            other => other,
        }
    }

    /// Runs one operation and reads its typed result.
    pub fn run<O: Operation>(&self, operation: &O) -> Result<O::Output, Error> {
        let request = operation.request()?;
        O::Output::from_outcome(self.call(&request, operation.timeout_ms())?)
    }

    /// `capabilities_get`.
    pub fn capabilities(&self) -> Result<Capabilities, Error> {
        self.run(&CapabilitiesGet)
    }

    /// `memory_get_context`: owner-scoped recall.
    pub fn get_context(&self, request: &GetContext) -> Result<Recall, Error> {
        self.run(request)
    }

    /// `memory_remember`: the owner's own words, through the journal.
    pub fn remember(&self, request: &Remember) -> Result<WriteResult, Error> {
        self.run(request)
    }

    /// `memory_operation_get`: one operation's receipt.
    pub fn operation(&self, request: &OperationGet) -> Result<WriteResult, Error> {
        self.run(request)
    }

    /// `memory_operation_resolve`: keeps what a partial or unknown write wrote.
    pub fn resolve(&self, request: &OperationResolve) -> Result<WriteResult, Error> {
        self.run(request)
    }

    /// `memory_forget`.
    pub fn forget(&self, request: &Forget) -> Result<WriteResult, Error> {
        self.run(request)
    }

    /// `memory_erase_owner`.
    pub fn erase_owner(&self, request: &EraseOwner) -> Result<WriteResult, Error> {
        self.run(request)
    }

    /// `schema_status` (profile `admin`).
    pub fn schema_status(&self) -> Result<Outcome, Error> {
        self.run(&SchemaStatus::default())
    }

    /// `schema_bootstrap` (profile `admin`), with 120 s of job time.
    pub fn schema_bootstrap(&self) -> Result<Outcome, Error> {
        self.run(&SchemaBootstrap::default())
    }

    // Lifetime --------------------------------------------------------------------------------------------------

    /// Stops admission, waits up to `wait` while the library answers BUSY (work still running), then disposes the
    /// context. Idempotent. On [`CallError::Busy`] the context stays closing: call again to finish.
    pub fn close(&self, wait: Duration) -> Result<(), Error> {
        let shared = &self.shared;
        let wakers = {
            let mut tracking = shared.lock();
            if tracking.closed {
                return Ok(());
            }
            tracking.closing = true;
            Shared::take_wakers(&mut tracking)
        };
        shared.changed.notify_all();
        wakers.into_iter().for_each(Waker::wake);

        let deadline = Instant::now() + wait;
        let result = loop {
            let status = shared.native.close(shared.handle);
            match status {
                sys::AM_V1_OK | sys::AM_V1_INVALID_HANDLE => break Ok(()),
                sys::AM_V1_BUSY if Instant::now() < deadline => thread::sleep(CLOSE_RETRY),
                other => break check("am_v1_close", other),
            }
        };
        // Closing makes am_v1_wait return at once, so the waiter ends promptly.
        let waiter = {
            let mut tracking = shared.lock();
            tracking.closed = result.is_ok();
            tracking.waiter.take()
        };
        if let Some(waiter) = waiter {
            let _ = waiter.join();
        }
        result
    }
}

impl Drop for Context {
    /// Closes, waiting up to [`DEFAULT_CLOSE_WAIT`] for running work. If work is still running then, the context is
    /// left closing in the library (it refuses new work) and the store stays held until the process ends.
    fn drop(&mut self) {
        let _ = self.close(DEFAULT_CLOSE_WAIT);
    }
}

impl Job {
    /// The job handle, an opaque unsigned 64-bit value.
    pub fn handle(&self) -> u64 {
        self.handle
    }

    /// The result once the job has one; waits at most `wait_ms` inside the library. Every terminal poll returns a
    /// new copy of the result.
    pub fn poll(&self, wait_ms: u32) -> Result<Polled, Error> {
        self.shared.poll(self.handle, wait_ms)
    }

    /// Blocks until the result arrives, through the context's waiter thread. [`Error::Timeout`] after `timeout`
    /// (the job keeps running; cancel it if it should stop); [`Error::Closed`] when the context starts closing first.
    pub fn wait(&self, timeout: Duration) -> Result<Outcome, Error> {
        let deadline = Instant::now() + timeout;
        let timed_out = || Error::Timeout {
            job: self.handle,
            waited: timeout,
        };
        loop {
            // Poll first: a job that finished before anyone waited may have been reported to the waiter already.
            if let Polled::Terminal(outcome) = self.poll(0)? {
                return Ok(outcome);
            }
            match self.shared.block_until_finished(self.handle, deadline) {
                Readiness::Finished => {}
                Readiness::Closing => return self.last_poll(Error::Closed),
                Readiness::Waiting => return self.last_poll(timed_out()),
                // The waiter has stopped: poll in this thread until the deadline, then say why the waiter stopped.
                Readiness::Failed(error) => loop {
                    if let Polled::Terminal(outcome) = self.poll(50)? {
                        return Ok(outcome);
                    }
                    if Instant::now() >= deadline {
                        return Err(Error::Call {
                            function: "am_v1_wait",
                            error,
                        });
                    }
                },
            }
        }
    }

    fn last_poll(&self, otherwise: Error) -> Result<Outcome, Error> {
        match self.poll(0) {
            Ok(Polled::Terminal(outcome)) => Ok(outcome),
            _ => Err(otherwise),
        }
    }

    /// Asks the job to stop. Cooperative: its result then says `cancelled` unless it had already finished. A handle
    /// the library no longer knows (released, or its context closed) is not an error.
    pub fn cancel(&self) -> Result<(), Error> {
        ignore_invalid_handle(check(
            "am_v1_cancel",
            self.shared.native.cancel(self.shared.handle, self.handle),
        ))
    }

    /// Gives up the handle now. Unfinished work stays the library's to finish, and keeps its capacity until it does.
    pub fn release(mut self) -> Result<(), Error> {
        self.release_now()
    }

    fn release_now(&mut self) -> Result<(), Error> {
        if self.released {
            return Ok(());
        }
        self.released = true;
        self.shared.lock().jobs.remove(&self.handle);
        ignore_invalid_handle(check(
            "am_v1_job_release",
            self.shared.native.job_release(self.shared.handle, self.handle),
        ))
    }

    #[cfg(feature = "async")]
    pub(crate) fn shared(&self) -> &Arc<Shared> {
        &self.shared
    }
}

impl Drop for Job {
    fn drop(&mut self) {
        let _ = self.release_now();
    }
}

fn ignore_invalid_handle(result: Result<(), Error>) -> Result<(), Error> {
    match result {
        Err(Error::Call {
            error: CallError::InvalidHandle,
            ..
        }) => Ok(()),
        other => other,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::sync::Mutex as StdMutex;

    /// One context per store per process: the tests of this binary take turns.
    pub(crate) static STORE: StdMutex<()> = StdMutex::new(());

    pub(crate) fn config() -> Value {
        json!({
            "schemaVersion": 1, "profile": "read_only", "owner": {"mode": "fixed", "id": "gate-owner-a"},
            "store": {"uri": "bolt://127.0.0.1:9", "username": "neo4j", "password": "unused", "verifyConnectivity": false},
            "embedding": {"dimensions": 8, "model": "gate-unit-axes"}
        })
    }

    fn capabilities() -> Value {
        json!({"schemaVersion": 1, "operation": "capabilities_get"})
    }

    #[test]
    fn the_poll_fallback_delivers_results_without_am_v1_wait() {
        let _turn = STORE.lock().unwrap_or_else(PoisonError::into_inner);
        let native = Native::load().unwrap_or_else(|error| panic!("{error}"));
        let context = Context::open_inner(native, &config(), false).unwrap();
        for _ in 0..20 {
            let outcome = context.call(&capabilities(), 0).unwrap();
            assert!(outcome.is_ok(), "{:?}", outcome.document);
        }
        context.close(DEFAULT_CLOSE_WAIT).unwrap();
    }

    #[test]
    fn the_waiter_starts_at_the_first_wait_and_ends_with_close() {
        let _turn = STORE.lock().unwrap_or_else(PoisonError::into_inner);
        let context = Context::open(&config()).unwrap();
        assert!(
            context.shared.lock().waiter.is_none(),
            "no thread until something waits"
        );
        let job = context.submit(&capabilities(), 0).unwrap();
        let outcome = job.wait(Duration::from_secs(10)).unwrap();
        assert!(outcome.is_ok());
        assert!(context.shared.lock().waiter.is_some());
        drop(job);
        let started = Instant::now();
        context.close(DEFAULT_CLOSE_WAIT).unwrap();
        assert!(
            started.elapsed() < Duration::from_secs(2),
            "close joined the waiter in {:?}",
            started.elapsed()
        );
        assert!(context.shared.lock().waiter.is_none());
        assert!(
            context.shared.lock().jobs.is_empty(),
            "a dropped job is released and forgotten"
        );
    }

    #[test]
    fn a_closed_context_refuses_submits_and_closes_again_quietly() {
        let _turn = STORE.lock().unwrap_or_else(PoisonError::into_inner);
        let context = Context::open(&config()).unwrap();
        context.close(DEFAULT_CLOSE_WAIT).unwrap();
        let error = context.submit(&capabilities(), 0).unwrap_err();
        assert_eq!(error.call_error(), Some(CallError::InvalidHandle), "{error}");
        context.close(DEFAULT_CLOSE_WAIT).unwrap();
    }
}
