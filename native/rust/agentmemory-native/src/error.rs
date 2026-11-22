//! The first of the three status layers: what a C ABI call itself returned. An operation's own outcome is not an error
//! here; it is the `status` of its result (see [`crate::OperationStatus`]).

use std::fmt;
use std::time::Duration;

use agentmemory_native_sys as sys;

pub use sys::LoadError;

/// A C ABI call's return code other than `AM_V1_OK`. It describes the call, never the operation's outcome.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[non_exhaustive]
pub enum CallError {
    /// `AM_V1_INVALID_ARGUMENT`: null or bad pointer or length, malformed configuration, or a request too large.
    InvalidArgument,
    /// `AM_V1_UNSUPPORTED_ABI`: the requested ABI major version is not the library's, or the export is missing.
    UnsupportedAbi,
    /// `AM_V1_INVALID_HANDLE`: unknown, closed, released or foreign context or job handle.
    InvalidHandle,
    /// `AM_V1_OVERLOADED`: no capacity; the job was not admitted and nothing ran.
    Overloaded,
    /// `AM_V1_CLOSING`: the context is closing and admits no new work.
    Closing,
    /// `AM_V1_INTERNAL_ERROR`: an unexpected failure inside the library.
    InternalError,
    /// `AM_V1_BUSY`: open: the store is already held by a context; close: work has not settled yet.
    Busy,
    /// A code this binding does not know.
    Unknown(i32),
}

impl CallError {
    /// The error for a return code; `None` for `AM_V1_OK`.
    pub fn from_code(code: i32) -> Option<CallError> {
        Some(match code {
            sys::AM_V1_OK => return None,
            sys::AM_V1_INVALID_ARGUMENT => CallError::InvalidArgument,
            sys::AM_V1_UNSUPPORTED_ABI => CallError::UnsupportedAbi,
            sys::AM_V1_INVALID_HANDLE => CallError::InvalidHandle,
            sys::AM_V1_OVERLOADED => CallError::Overloaded,
            sys::AM_V1_CLOSING => CallError::Closing,
            sys::AM_V1_INTERNAL_ERROR => CallError::InternalError,
            sys::AM_V1_BUSY => CallError::Busy,
            other => CallError::Unknown(other),
        })
    }

    /// The return code.
    pub fn code(self) -> i32 {
        match self {
            CallError::InvalidArgument => sys::AM_V1_INVALID_ARGUMENT,
            CallError::UnsupportedAbi => sys::AM_V1_UNSUPPORTED_ABI,
            CallError::InvalidHandle => sys::AM_V1_INVALID_HANDLE,
            CallError::Overloaded => sys::AM_V1_OVERLOADED,
            CallError::Closing => sys::AM_V1_CLOSING,
            CallError::InternalError => sys::AM_V1_INTERNAL_ERROR,
            CallError::Busy => sys::AM_V1_BUSY,
            CallError::Unknown(code) => code,
        }
    }
}

impl fmt::Display for CallError {
    /// The ABI's name: `INVALID_ARGUMENT`, ..., `UNKNOWN(n)`.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let name = match self {
            CallError::InvalidArgument => "INVALID_ARGUMENT",
            CallError::UnsupportedAbi => "UNSUPPORTED_ABI",
            CallError::InvalidHandle => "INVALID_HANDLE",
            CallError::Overloaded => "OVERLOADED",
            CallError::Closing => "CLOSING",
            CallError::InternalError => "INTERNAL_ERROR",
            CallError::Busy => "BUSY",
            CallError::Unknown(code) => return write!(f, "UNKNOWN({code})"),
        };
        f.write_str(name)
    }
}

impl std::error::Error for CallError {}

/// Everything that can go wrong in this crate.
#[derive(Debug)]
#[non_exhaustive]
pub enum Error {
    /// The library could not be found or loaded, or is not an AgentMemory native library of ABI 1.x.
    Library(LoadError),
    /// A C ABI call returned a code other than `AM_V1_OK`.
    Call {
        /// The export, for example `am_v1_submit`.
        function: &'static str,
        /// What it returned.
        error: CallError,
    },
    /// `am_v1_open` refused the configuration or failed; `code` and `message` come from the library's error document.
    Open {
        /// What `am_v1_open` returned.
        error: CallError,
        /// The error document's code, for example `authority_held` or `unknown_property` (`unknown` without one).
        code: String,
        /// The error document's message.
        message: String,
    },
    /// The request could not be written as the contract's JSON (a vector with a value that is not a finite number).
    Request(String),
    /// A result document is not the JSON the contract describes.
    Decode(String),
    /// No result came within the time this binding waited.
    Timeout {
        /// The job.
        job: u64,
        /// How long it waited.
        waited: Duration,
    },
    /// The context started closing before the job had a result.
    Closed,
}

impl Error {
    /// The call error, for [`Error::Call`] and [`Error::Open`].
    pub fn call_error(&self) -> Option<CallError> {
        match self {
            Error::Call { error, .. } | Error::Open { error, .. } => Some(*error),
            _ => None,
        }
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::Library(error) => error.fmt(f),
            Error::Call { function, error } => write!(f, "{function} returned {error}"),
            Error::Open { error, code, message } => write!(f, "am_v1_open returned {error}: {code}: {message}"),
            Error::Request(text) => write!(f, "the request cannot be sent: {text}"),
            Error::Decode(text) => write!(f, "the library returned a document this binding cannot read: {text}"),
            Error::Timeout { job, waited } => {
                write!(f, "job {job:#x} delivered no result within {} ms", waited.as_millis())
            }
            Error::Closed => f.write_str("the context is closing; the job's result will not be delivered"),
        }
    }
}

impl std::error::Error for Error {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Error::Library(error) => Some(error),
            Error::Call { error, .. } | Error::Open { error, .. } => Some(error),
            _ => None,
        }
    }
}

impl From<LoadError> for Error {
    fn from(error: LoadError) -> Error {
        Error::Library(error)
    }
}

/// `Ok` for `AM_V1_OK`, else [`Error::Call`] naming the function.
pub(crate) fn check(function: &'static str, code: i32) -> Result<(), Error> {
    match CallError::from_code(code) {
        None => Ok(()),
        Some(error) => Err(Error::Call { function, error }),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_code_round_trips() {
        for code in 1..=7 {
            assert_eq!(CallError::from_code(code).unwrap().code(), code);
        }
        assert_eq!(CallError::from_code(0), None);
        assert_eq!(CallError::from_code(99), Some(CallError::Unknown(99)));
        assert_eq!(CallError::Overloaded.to_string(), "OVERLOADED");
    }

    #[test]
    fn a_failed_call_names_the_function_and_the_code() {
        let error = check("am_v1_submit", sys::AM_V1_CLOSING).unwrap_err();
        assert_eq!(error.to_string(), "am_v1_submit returned CLOSING");
        assert_eq!(error.call_error(), Some(CallError::Closing));
        assert!(check("am_v1_submit", sys::AM_V1_OK).is_ok());
    }
}
