//! Futures over the context's waiter thread (feature `async`). Standard futures and wakers only, so any executor runs
//! them; nothing here blocks the polling thread.
//!
//! Dropping a future before it completes is how a caller cancels in Rust: the job is cancelled in the library and its
//! handle released. There is no binding-side deadline: the job's own `timeout_ms` ends it in the library, and an
//! executor's own timeout can drop the future sooner.

use std::future::{Future, IntoFuture};
use std::marker::PhantomData;
use std::pin::Pin;
use std::sync::Arc;
use std::task::{Context as TaskContext, Poll};

use serde::Serialize;

use crate::context::{Readiness, Shared};
use crate::requests::Operation;
use crate::results::{FromOutcome, Outcome, Polled};
use crate::{Context, Error, Job};

/// A job's result, as a future. From [`Context::call_async`], or `job.await` on a [`Job`].
#[derive(Debug)]
#[must_use = "a future does nothing unless polled; dropping it cancels the job"]
pub struct JobFuture {
    job: Option<Job>,
    error: Option<Error>,
    registered: bool,
}

impl JobFuture {
    fn new(job: Result<Job, Error>) -> JobFuture {
        match job {
            Ok(job) => JobFuture {
                job: Some(job),
                error: None,
                registered: false,
            },
            Err(error) => JobFuture {
                job: None,
                error: Some(error),
                registered: false,
            },
        }
    }

    /// The job's handle, while the future holds it.
    pub fn handle(&self) -> Option<u64> {
        self.job.as_ref().map(Job::handle)
    }

    /// Stops waiting and releases the job (dropping it).
    fn finish(&mut self) {
        if let Some(job) = self.job.take() {
            job.shared().unregister(job.handle(), &mut self.registered);
        }
    }
}

impl Future for JobFuture {
    type Output = Result<Outcome, Error>;

    fn poll(self: Pin<&mut Self>, cx: &mut TaskContext<'_>) -> Poll<Self::Output> {
        let this = self.get_mut();
        if let Some(error) = this.error.take() {
            return Poll::Ready(Err(error));
        }
        let (shared, handle): (Arc<Shared>, u64) = match &this.job {
            Some(job) => (Arc::clone(job.shared()), job.handle()),
            None => panic!("JobFuture polled after it completed"),
        };
        let ready = |this: &mut JobFuture, result: Result<Outcome, Error>| {
            this.finish();
            Poll::Ready(result)
        };
        match shared.poll(handle, 0) {
            Err(error) => return ready(this, Err(error)),
            Ok(Polled::Terminal(outcome)) => return ready(this, Ok(outcome)),
            Ok(Polled::Pending) => {}
        }
        match shared.register(handle, cx.waker(), &mut this.registered) {
            Readiness::Waiting => Poll::Pending,
            // Reported finished since the poll above: poll again on the next turn.
            Readiness::Finished => {
                cx.waker().wake_by_ref();
                Poll::Pending
            }
            Readiness::Closing => ready(this, Err(Error::Closed)),
            Readiness::Failed(error) => ready(
                this,
                Err(Error::Call {
                    function: "am_v1_wait",
                    error,
                }),
            ),
        }
    }
}

impl Drop for JobFuture {
    /// A future dropped before its result: the job is cancelled in the library, then released.
    fn drop(&mut self) {
        if let Some(job) = &self.job {
            let _ = job.cancel();
        }
        self.finish();
    }
}

impl IntoFuture for Job {
    type Output = Result<Outcome, Error>;
    type IntoFuture = JobFuture;

    /// The job's result; dropping the future cancels and releases the job.
    fn into_future(self) -> JobFuture {
        JobFuture::new(Ok(self))
    }
}

/// An operation's typed result, as a future. From [`Context::run_async`].
#[derive(Debug)]
#[must_use = "a future does nothing unless polled; dropping it cancels the job"]
pub struct OperationFuture<T> {
    inner: JobFuture,
    output: PhantomData<fn() -> T>,
}

impl<T: FromOutcome> Future for OperationFuture<T> {
    type Output = Result<T, Error>;

    fn poll(self: Pin<&mut Self>, cx: &mut TaskContext<'_>) -> Poll<Self::Output> {
        let this = self.get_mut();
        Pin::new(&mut this.inner)
            .poll(cx)
            .map(|result| result.and_then(T::from_outcome))
    }
}

impl Context {
    /// Submits now and returns the result as a future. A refused submit is the future's error.
    pub fn call_async(&self, request: &impl Serialize, timeout_ms: u32) -> JobFuture {
        JobFuture::new(self.submit(request, timeout_ms))
    }

    /// Submits one operation now and returns its typed result as a future.
    pub fn run_async<O: Operation>(&self, operation: &O) -> OperationFuture<O::Output> {
        let job = operation
            .request()
            .and_then(|request| self.submit(&request, operation.timeout_ms()));
        OperationFuture {
            inner: JobFuture::new(job),
            output: PhantomData,
        }
    }
}
