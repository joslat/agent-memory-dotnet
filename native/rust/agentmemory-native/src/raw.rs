//! The C ABI one to one, as safe functions: return codes as `i32`, handles as `u64`, documents as bytes.
//!
//! This is the only module of the crate with `unsafe` code. Every pointer the library is given comes from a Rust slice
//! or a local output, and every buffer it returns is copied and then freed exactly once with `am_v1_buffer_free`. The
//! library checks every handle (an unknown, closed or released one is refused with `AM_V1_INVALID_HANDLE`), so any
//! `u64` is a safe argument. The gate runner drives the library through this module; [`crate::Context`] is built on it.
#![allow(unsafe_code)]

use std::path::Path;
use std::ptr;

use agentmemory_native_sys as sys;

pub use sys::{LoadError, AM_V1_ABI_VERSION as ABI_VERSION};

/// A loaded library. Cheap to copy: the library is never unloaded.
#[derive(Clone, Copy, Debug)]
pub struct Native {
    library: &'static sys::Library,
}

/// What `am_v1_open` returned.
#[derive(Debug)]
pub struct OpenOutput {
    /// The call's return code.
    pub status: i32,
    /// The new context's handle; 0 unless `status` is `AM_V1_OK`.
    pub context: u64,
    /// The error document, when the library wrote one.
    pub error: Option<Vec<u8>>,
}

/// What `am_v1_poll` returned.
#[derive(Debug)]
pub struct PollOutput {
    /// The call's return code.
    pub status: i32,
    /// `AM_V1_PENDING` or `AM_V1_TERMINAL`.
    pub disposition: u32,
    /// The result document, when there is one.
    pub result: Option<Vec<u8>>,
}

/// What `am_v1_state` returned.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StateOutput {
    /// The call's return code.
    pub status: i32,
    /// The state revision.
    pub state_revision: u64,
    /// The policy epoch.
    pub policy_epoch: u64,
    /// The deletion epoch.
    pub deletion_epoch: u64,
    /// The memory revision.
    pub memory_revision: u64,
    /// The `AM_V1_*` state flags.
    pub flags: u32,
}

/// A buffer the library returned. Freed exactly once, when dropped; a null pointer is never passed to the library.
struct Buffer {
    free: sys::am_v1_buffer_free_fn,
    pointer: *mut u8,
    length: usize,
}

impl Buffer {
    /// A copy of the bytes, or `None` for a null pointer.
    fn copy(&self) -> Option<Vec<u8>> {
        if self.pointer.is_null() {
            return None;
        }
        // SAFETY: the library returned this pointer with this length, owned by the caller and readable until it is
        // freed, which happens only in drop, after this copy.
        Some(unsafe { std::slice::from_raw_parts(self.pointer, self.length) }.to_vec())
    }
}

impl Drop for Buffer {
    fn drop(&mut self) {
        if !self.pointer.is_null() {
            // SAFETY: a buffer the library returned, freed here exactly once (Buffer is not Clone).
            unsafe { (self.free)(self.pointer.cast()) };
        }
    }
}

/// The pointer for a slice: null for an empty one, so the library is never handed a dangling address.
fn bytes_pointer(bytes: &[u8]) -> *const u8 {
    if bytes.is_empty() {
        ptr::null()
    } else {
        bytes.as_ptr()
    }
}

impl Native {
    /// Loads the library from `AGENTMEMORY_NATIVE_LIBRARY`, else from its platform file name next to the executable.
    pub fn load() -> Result<Native, LoadError> {
        sys::Library::load_default().map(|library| Native { library })
    }

    /// Loads the library at `path`.
    pub fn load_from(path: impl AsRef<Path>) -> Result<Native, LoadError> {
        sys::Library::load(path).map(|library| Native { library })
    }

    /// The raw declarations, for what this module does not cover.
    pub fn library(&self) -> &'static sys::Library {
        self.library
    }

    /// The file the library was loaded from.
    pub fn path(&self) -> &'static Path {
        self.library.path()
    }

    /// Whether the library has `am_v1_wait` (ABI 1.1).
    pub fn has_wait(&self) -> bool {
        self.library.has_wait()
    }

    fn buffer(&self, pointer: *mut u8, length: usize) -> Buffer {
        Buffer {
            free: self.library.am_v1_buffer_free,
            pointer,
            length,
        }
    }

    /// `am_v1_abi_version`: `(major << 16) | minor`.
    pub fn abi_version(&self) -> u32 {
        // SAFETY: takes nothing and returns a constant.
        unsafe { (self.library.am_v1_abi_version)() }
    }

    /// `am_v1_open` with the requested ABI and a UTF-8 JSON configuration.
    pub fn open(&self, requested_abi: u32, config: &[u8]) -> OpenOutput {
        let mut context = 0_u64;
        let mut error: *mut u8 = ptr::null_mut();
        let mut error_len = 0_usize;
        // SAFETY: the configuration pointer is valid for its length (or null when empty); the outputs are writable
        // locals.
        let status = unsafe {
            (self.library.am_v1_open)(
                requested_abi,
                bytes_pointer(config),
                config.len(),
                &mut context,
                &mut error,
                &mut error_len,
            )
        };
        let error = self.buffer(error, error_len).copy();
        OpenOutput { status, context, error }
    }

    /// `am_v1_submit`: the return code and the new job's handle (0 unless admitted).
    pub fn submit(&self, context: u64, request: &[u8], remaining_timeout_ms: u32) -> (i32, u64) {
        let mut job = 0_u64;
        // SAFETY: the request pointer is valid for its length (or null when empty; the library copies the bytes before
        // it returns); the output is a writable local.
        let status = unsafe {
            (self.library.am_v1_submit)(
                context,
                bytes_pointer(request),
                request.len(),
                remaining_timeout_ms,
                &mut job,
            )
        };
        (status, job)
    }

    /// `am_v1_poll`, waiting at most `wait_ms` inside the library.
    pub fn poll(&self, context: u64, job: u64, wait_ms: u32) -> PollOutput {
        let mut disposition = sys::AM_V1_PENDING;
        let mut result: *mut u8 = ptr::null_mut();
        let mut result_len = 0_usize;
        // SAFETY: the outputs are writable locals.
        let status =
            unsafe { (self.library.am_v1_poll)(context, job, wait_ms, &mut disposition, &mut result, &mut result_len) };
        let result = self.buffer(result, result_len).copy();
        PollOutput {
            status,
            disposition,
            result,
        }
    }

    /// `am_v1_wait` (ABI 1.1): the return code and a finished job (0 for none). `None` when the library is ABI 1.0.
    pub fn wait(&self, context: u64, wait_ms: u32) -> Option<(i32, u64)> {
        let wait = self.library.am_v1_wait?;
        let mut job = 0_u64;
        // SAFETY: the output is a writable local.
        let status = unsafe { wait(context, wait_ms, &mut job) };
        Some((status, job))
    }

    /// `am_v1_cancel`.
    pub fn cancel(&self, context: u64, job: u64) -> i32 {
        // SAFETY: takes handles only.
        unsafe { (self.library.am_v1_cancel)(context, job) }
    }

    /// `am_v1_job_release`.
    pub fn job_release(&self, context: u64, job: u64) -> i32 {
        // SAFETY: takes handles only.
        unsafe { (self.library.am_v1_job_release)(context, job) }
    }

    /// `am_v1_state`.
    pub fn state(&self, context: u64) -> StateOutput {
        let mut out = StateOutput {
            status: 0,
            state_revision: 0,
            policy_epoch: 0,
            deletion_epoch: 0,
            memory_revision: 0,
            flags: 0,
        };
        // SAFETY: the outputs are writable fields of a local.
        out.status = unsafe {
            (self.library.am_v1_state)(
                context,
                &mut out.state_revision,
                &mut out.policy_epoch,
                &mut out.deletion_epoch,
                &mut out.memory_revision,
                &mut out.flags,
            )
        };
        out
    }

    /// `am_v1_close`: `AM_V1_BUSY` while work is still running; call again.
    pub fn close(&self, context: u64) -> i32 {
        // SAFETY: takes a handle only.
        unsafe { (self.library.am_v1_close)(context) }
    }
}

/// The name of a return code: `OK`, `INVALID_ARGUMENT`, ..., or `UNKNOWN(n)`.
pub fn status_name(status: i32) -> String {
    match crate::CallError::from_code(status) {
        None => "OK".to_owned(),
        Some(error) => error.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn r_free_null_a_null_buffer_is_never_freed_and_copies_as_nothing() {
        let native = Native::load().unwrap_or_else(|error| panic!("{error}"));
        // Dropped at once: the binding's buffer handling must not hand NULL (or anything else) to the library here.
        let buffer = native.buffer(ptr::null_mut(), 0);
        assert!(buffer.copy().is_none());
        drop(buffer);
        // And the library itself ignores NULL, as the header says.
        // SAFETY: am_v1_buffer_free ignores NULL.
        unsafe { (native.library.am_v1_buffer_free)(ptr::null_mut()) };
    }

    #[test]
    fn status_names_are_the_abi_names() {
        assert_eq!(status_name(sys::AM_V1_OK), "OK");
        assert_eq!(status_name(sys::AM_V1_INVALID_HANDLE), "INVALID_HANDLE");
        assert_eq!(status_name(sys::AM_V1_BUSY), "BUSY");
        assert_eq!(status_name(42), "UNKNOWN(42)");
    }
}
