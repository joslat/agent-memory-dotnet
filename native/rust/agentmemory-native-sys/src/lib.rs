//! Raw declarations of the AgentMemory native library's C ABI, version 1.1 (`native/include/agentmemory_native.h`).
//!
//! The library is a .NET Native AOT shared library. This crate does not link it: [`Library::load`] and
//! [`Library::load_default`] open it at run time with `libloading`, look up every export, check the ABI major
//! version, and keep it loaded for the rest of the process. It is never unloaded, as the header requires.
//!
//! The library comes from a known place only: an explicit path, else the `AGENTMEMORY_NATIVE_LIBRARY` environment
//! variable, else the platform's file name next to the running executable. Nothing searches the loader's paths or the
//! working directory.
//!
//! Every function here is the raw C function. The `agentmemory-native` crate wraps them in a safe API.

#![allow(non_camel_case_types)]
#![warn(missing_docs)]

use std::collections::HashMap;
use std::ffi::{c_void, OsString};
use std::fmt;
use std::mem::ManuallyDrop;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock, PoisonError};

/// ABI major version this crate declares.
pub const AM_V1_ABI_MAJOR: u32 = 1;
/// ABI minor version this crate declares.
pub const AM_V1_ABI_MINOR: u32 = 1;
/// `(major << 16) | minor`, as `am_v1_abi_version` reports it and `am_v1_open` takes it.
pub const AM_V1_ABI_VERSION: u32 = (AM_V1_ABI_MAJOR << 16) | AM_V1_ABI_MINOR;

/// An opaque context handle. Never reused within a process.
pub type am_v1_context_t = u64;
/// An opaque job handle. Never reused within a process.
pub type am_v1_job_t = u64;

/// The call did what it says.
pub const AM_V1_OK: i32 = 0;
/// Null or bad pointer or length, malformed UTF-8 or JSON, or a request the schema refuses.
pub const AM_V1_INVALID_ARGUMENT: i32 = 1;
/// The requested ABI major version is not this library's.
pub const AM_V1_UNSUPPORTED_ABI: i32 = 2;
/// Unknown, closed, released or foreign context or job handle.
pub const AM_V1_INVALID_HANDLE: i32 = 3;
/// No capacity: the job was not admitted. Nothing ran.
pub const AM_V1_OVERLOADED: i32 = 4;
/// The context is closing and admits no new work.
pub const AM_V1_CLOSING: i32 = 5;
/// An unexpected failure inside the library.
pub const AM_V1_INTERNAL_ERROR: i32 = 6;
/// open: the authority is already held. close: work has not settled yet; call again.
pub const AM_V1_BUSY: i32 = 7;

/// Poll disposition: no result yet.
pub const AM_V1_PENDING: u32 = 0;
/// Poll disposition: a result is available; the caller owns the returned buffer.
pub const AM_V1_TERMINAL: u32 = 1;

/// State flag: recall may run.
pub const AM_V1_READ_ALLOWED: u32 = 1;
/// State flag: writes are admitted (the write profiles; `read_only` never sets it).
pub const AM_V1_ADMISSION_ALLOWED: u32 = 2;
/// State flag: no write is accepted or running.
pub const AM_V1_WRITE_QUIESCENT: u32 = 4;
/// State flag: the context is closing.
pub const AM_V1_CLOSING_FLAG: u32 = 8;
/// State flag: an owner erase is pending.
pub const AM_V1_ERASE_PENDING: u32 = 16;

/// `uint32_t am_v1_abi_version(void)`
pub type am_v1_abi_version_fn = unsafe extern "C" fn() -> u32;
/// `int32_t am_v1_open(uint32_t, const uint8_t *, size_t, am_v1_context_t *, uint8_t **, size_t *)`
pub type am_v1_open_fn = unsafe extern "C" fn(
    requested_abi: u32,
    config_utf8: *const u8,
    config_len: usize,
    out_context: *mut am_v1_context_t,
    out_error_utf8: *mut *mut u8,
    out_error_len: *mut usize,
) -> i32;
/// `int32_t am_v1_submit(am_v1_context_t, const uint8_t *, size_t, uint32_t, am_v1_job_t *)`
pub type am_v1_submit_fn = unsafe extern "C" fn(
    context: am_v1_context_t,
    request_utf8: *const u8,
    request_len: usize,
    remaining_timeout_ms: u32,
    out_job: *mut am_v1_job_t,
) -> i32;
/// `int32_t am_v1_poll(am_v1_context_t, am_v1_job_t, uint32_t, uint32_t *, uint8_t **, size_t *)`
pub type am_v1_poll_fn = unsafe extern "C" fn(
    context: am_v1_context_t,
    job: am_v1_job_t,
    wait_ms: u32,
    out_disposition: *mut u32,
    out_result_utf8: *mut *mut u8,
    out_result_len: *mut usize,
) -> i32;
/// `int32_t am_v1_wait(am_v1_context_t, uint32_t, am_v1_job_t *)` (ABI 1.1)
pub type am_v1_wait_fn = unsafe extern "C" fn(context: am_v1_context_t, wait_ms: u32, out_job: *mut am_v1_job_t) -> i32;
/// `int32_t am_v1_cancel(am_v1_context_t, am_v1_job_t)`
pub type am_v1_cancel_fn = unsafe extern "C" fn(context: am_v1_context_t, job: am_v1_job_t) -> i32;
/// `int32_t am_v1_job_release(am_v1_context_t, am_v1_job_t)`
pub type am_v1_job_release_fn = unsafe extern "C" fn(context: am_v1_context_t, job: am_v1_job_t) -> i32;
/// `void am_v1_buffer_free(void *)`
pub type am_v1_buffer_free_fn = unsafe extern "C" fn(buffer: *mut c_void);
/// `int32_t am_v1_state(am_v1_context_t, uint64_t *, uint64_t *, uint64_t *, uint64_t *, uint32_t *)`
pub type am_v1_state_fn = unsafe extern "C" fn(
    context: am_v1_context_t,
    out_state_revision: *mut u64,
    out_policy_epoch: *mut u64,
    out_deletion_epoch: *mut u64,
    out_memory_revision: *mut u64,
    out_flags: *mut u32,
) -> i32;
/// `int32_t am_v1_close(am_v1_context_t)`
pub type am_v1_close_fn = unsafe extern "C" fn(context: am_v1_context_t) -> i32;

/// The library's file name on this platform.
#[cfg(windows)]
pub const LIBRARY_FILE_NAME: &str = "agentmemory_native.dll";
/// The library's file name on this platform.
#[cfg(target_os = "macos")]
pub const LIBRARY_FILE_NAME: &str = "agentmemory_native.dylib";
/// The library's file name on this platform.
#[cfg(not(any(windows, target_os = "macos")))]
pub const LIBRARY_FILE_NAME: &str = "agentmemory_native.so";

/// The environment variable that names the library to load.
pub const LIBRARY_ENV: &str = "AGENTMEMORY_NATIVE_LIBRARY";

/// A loaded AgentMemory native library: its path and its exports.
///
/// Values of this type live for the whole process ([`Library::load`] returns `&'static Library`): the library is never
/// unloaded, so the function pointers stay valid.
#[derive(Debug)]
pub struct Library {
    path: PathBuf,
    /// `am_v1_abi_version`
    pub am_v1_abi_version: am_v1_abi_version_fn,
    /// `am_v1_open`
    pub am_v1_open: am_v1_open_fn,
    /// `am_v1_submit`
    pub am_v1_submit: am_v1_submit_fn,
    /// `am_v1_poll`
    pub am_v1_poll: am_v1_poll_fn,
    /// `am_v1_wait`, ABI 1.1. `None` for a 1.0 library, which has no such export.
    pub am_v1_wait: Option<am_v1_wait_fn>,
    /// `am_v1_cancel`
    pub am_v1_cancel: am_v1_cancel_fn,
    /// `am_v1_job_release`
    pub am_v1_job_release: am_v1_job_release_fn,
    /// `am_v1_buffer_free`
    pub am_v1_buffer_free: am_v1_buffer_free_fn,
    /// `am_v1_state`
    pub am_v1_state: am_v1_state_fn,
    /// `am_v1_close`
    pub am_v1_close: am_v1_close_fn,
}

/// Where a library path came from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum Source {
    /// A path the caller gave.
    Explicit,
    /// The `AGENTMEMORY_NATIVE_LIBRARY` environment variable.
    Environment,
    /// The platform's file name next to the running executable.
    NextToExecutable,
}

/// Why the library could not be used.
#[derive(Debug)]
#[non_exhaustive]
pub enum LoadError {
    /// There is no file at the path.
    NotFound {
        /// The path looked at.
        path: PathBuf,
        /// Where the path came from.
        source: Source,
    },
    /// The file exists but this platform cannot load it (another architecture or platform, or damaged).
    Unloadable {
        /// The file.
        path: PathBuf,
        /// The loader's own message.
        reason: String,
    },
    /// The file loads but lacks an export every AgentMemory native library has.
    NotAgentMemory {
        /// The file.
        path: PathBuf,
        /// The first missing export.
        missing: &'static str,
    },
    /// The library implements another ABI major version.
    WrongAbi {
        /// The file.
        path: PathBuf,
        /// What `am_v1_abi_version` returned.
        version: u32,
    },
    /// The running executable's own path is unknown, so the library next to it cannot be found.
    NoExecutable {
        /// The operating system's message.
        reason: String,
    },
}

impl fmt::Display for LoadError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            LoadError::NotFound { path, source } => {
                write!(f, "AgentMemory native library not found at {}", path.display())?;
                match source {
                    Source::Explicit => Ok(()),
                    Source::Environment => write!(f, " (from {LIBRARY_ENV})"),
                    Source::NextToExecutable => write!(
                        f,
                        ": set {LIBRARY_ENV} to the library's path, or put {LIBRARY_FILE_NAME} next to the executable"
                    ),
                }
            }
            LoadError::Unloadable { path, reason } => write!(
                f,
                "cannot load the AgentMemory native library {} on {} {}: {reason}. Use the {LIBRARY_FILE_NAME} built for \
                 this platform, or set {LIBRARY_ENV} to a library built for it.",
                path.display(),
                std::env::consts::OS,
                std::env::consts::ARCH,
            ),
            LoadError::NotAgentMemory { path, missing } => write!(
                f,
                "{} is not an AgentMemory native library (no {missing} export)",
                path.display()
            ),
            LoadError::WrongAbi { path, version } => write!(
                f,
                "{} implements ABI {}.{}; this binding needs {AM_V1_ABI_MAJOR}.x",
                path.display(),
                version >> 16,
                version & 0xFFFF
            ),
            LoadError::NoExecutable { reason } => write!(
                f,
                "cannot find the running executable to look for {LIBRARY_FILE_NAME} next to it ({reason}); set {LIBRARY_ENV}"
            ),
        }
    }
}

impl std::error::Error for LoadError {}

/// The path [`Library::load_default`] loads, and where it came from, without touching the file.
pub fn default_path() -> Result<(PathBuf, Source), LoadError> {
    let executable = std::env::current_exe().map_err(|error| error.to_string());
    locate(std::env::var_os(LIBRARY_ENV), executable)
}

/// The resolution rule on its own, so it can be tested without changing the process environment.
fn locate(environment: Option<OsString>, executable: Result<PathBuf, String>) -> Result<(PathBuf, Source), LoadError> {
    if let Some(value) = environment.filter(|value| !value.is_empty()) {
        return Ok((PathBuf::from(value), Source::Environment));
    }
    let executable = executable.map_err(|reason| LoadError::NoExecutable { reason })?;
    let folder = executable.parent().unwrap_or_else(|| Path::new("."));
    Ok((folder.join(LIBRARY_FILE_NAME), Source::NextToExecutable))
}

/// The path handed to the loader. On Windows `canonicalize` gives a verbatim `\\?\C:\...` path; the plain `C:\...` form
/// names the same file and is what the Python and TypeScript bindings pass.
#[cfg(windows)]
fn loader_path(canonical: &Path) -> PathBuf {
    let plain = canonical
        .to_str()
        .and_then(|text| text.strip_prefix(r"\\?\"))
        .filter(|rest| rest.as_bytes().get(1) == Some(&b':'));
    plain.map_or_else(|| canonical.to_path_buf(), PathBuf::from)
}

/// The path handed to the loader: the canonical path itself.
#[cfg(not(windows))]
fn loader_path(canonical: &Path) -> PathBuf {
    canonical.to_path_buf()
}

/// Every library this process loaded, by canonical path. Entries are leaked on purpose: a library is never unloaded.
fn loaded() -> &'static Mutex<HashMap<PathBuf, &'static Library>> {
    static LOADED: OnceLock<Mutex<HashMap<PathBuf, &'static Library>>> = OnceLock::new();
    LOADED.get_or_init(|| Mutex::new(HashMap::new()))
}

impl Library {
    /// Loads the library from `AGENTMEMORY_NATIVE_LIBRARY`, else from [`LIBRARY_FILE_NAME`] next to the running
    /// executable. Loading the same file again returns the same library.
    pub fn load_default() -> Result<&'static Library, LoadError> {
        let (path, source) = default_path()?;
        Library::load_from(&path, source)
    }

    /// Loads the library at `path`. Loading the same file again returns the same library.
    pub fn load(path: impl AsRef<Path>) -> Result<&'static Library, LoadError> {
        Library::load_from(path.as_ref(), Source::Explicit)
    }

    fn load_from(path: &Path, source: Source) -> Result<&'static Library, LoadError> {
        let not_found = || LoadError::NotFound {
            path: path.to_path_buf(),
            source,
        };
        if !path.is_file() {
            return Err(not_found());
        }
        let canonical = std::fs::canonicalize(path).map_err(|_| not_found())?;
        let mut loaded = loaded().lock().unwrap_or_else(PoisonError::into_inner);
        if let Some(library) = loaded.get(&canonical) {
            return Ok(library);
        }
        let library: &'static Library = Box::leak(Box::new(Library::open(&loader_path(&canonical))?));
        loaded.insert(canonical, library);
        Ok(library)
    }

    /// Opens the file and looks up its exports. The handle is never closed, whatever happens.
    fn open(path: &Path) -> Result<Library, LoadError> {
        // SAFETY: loading a shared library runs its initialisers. The path is one the caller chose on purpose (an
        // explicit path, the environment variable, or the file next to the executable), never a search; the
        // AgentMemory library's initialisers are the .NET Native AOT runtime's own, which are safe to run.
        let handle = unsafe { libloading::Library::new(path) }.map_err(|error| LoadError::Unloadable {
            path: path.to_path_buf(),
            reason: error.to_string(),
        })?;
        // Never unloaded: the header forbids it, and the function pointers below must stay valid for the process.
        let handle = ManuallyDrop::new(handle);

        // SAFETY: the type is the header's exact signature for this export.
        let abi_version = unsafe { required::<am_v1_abi_version_fn>(&handle, path, "am_v1_abi_version") }?;
        // SAFETY: am_v1_abi_version takes nothing and returns a constant; it is safe to call at any time.
        let version = unsafe { abi_version() };
        if version >> 16 != AM_V1_ABI_MAJOR {
            return Err(LoadError::WrongAbi {
                path: path.to_path_buf(),
                version,
            });
        }

        // SAFETY (each lookup below): the type alias `<export>_fn` is the header's exact signature for that export.
        Ok(Library {
            path: path.to_path_buf(),
            am_v1_abi_version: abi_version,
            // SAFETY: see above.
            am_v1_open: unsafe { required::<am_v1_open_fn>(&handle, path, "am_v1_open") }?,
            // SAFETY: see above.
            am_v1_submit: unsafe { required::<am_v1_submit_fn>(&handle, path, "am_v1_submit") }?,
            // SAFETY: see above.
            am_v1_poll: unsafe { required::<am_v1_poll_fn>(&handle, path, "am_v1_poll") }?,
            // SAFETY: see above; a 1.0 library has no am_v1_wait, which is not an error.
            am_v1_wait: unsafe { required::<am_v1_wait_fn>(&handle, path, "am_v1_wait") }.ok(),
            // SAFETY: see above.
            am_v1_cancel: unsafe { required::<am_v1_cancel_fn>(&handle, path, "am_v1_cancel") }?,
            // SAFETY: see above.
            am_v1_job_release: unsafe { required::<am_v1_job_release_fn>(&handle, path, "am_v1_job_release") }?,
            // SAFETY: see above.
            am_v1_buffer_free: unsafe { required::<am_v1_buffer_free_fn>(&handle, path, "am_v1_buffer_free") }?,
            // SAFETY: see above.
            am_v1_state: unsafe { required::<am_v1_state_fn>(&handle, path, "am_v1_state") }?,
            // SAFETY: see above.
            am_v1_close: unsafe { required::<am_v1_close_fn>(&handle, path, "am_v1_close") }?,
        })
    }

    /// The file this library was loaded from (canonical).
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Whether the library has `am_v1_wait` (ABI 1.1).
    pub fn has_wait(&self) -> bool {
        self.am_v1_wait.is_some()
    }
}

/// Looks up one export as a function pointer of type `T`; a missing export makes the file "not an AgentMemory native
/// library".
///
/// # Safety
///
/// `T` must be the export's exact function pointer type, and `library` must never be unloaded while the pointer is
/// used (this crate never unloads a library).
unsafe fn required<T: Copy>(library: &libloading::Library, path: &Path, name: &'static str) -> Result<T, LoadError> {
    // SAFETY: the caller guarantees that T is the export's type; the symbol is copied out as a plain function pointer,
    // which stays valid because the library is never unloaded.
    match unsafe { library.get::<T>(name.as_bytes()) } {
        Ok(found) => Ok(*found),
        Err(_) => Err(LoadError::NotAgentMemory {
            path: path.to_path_buf(),
            missing: name,
        }),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_environment_variable_comes_first() {
        let (path, source) = locate(
            Some(OsString::from("/opt/am/agentmemory_native.so")),
            Ok(PathBuf::from("/usr/bin/app")),
        )
        .unwrap();
        assert_eq!(path, PathBuf::from("/opt/am/agentmemory_native.so"));
        assert_eq!(source, Source::Environment);
    }

    #[test]
    fn otherwise_the_file_next_to_the_executable() {
        let executable = PathBuf::from("bin").join("app");
        let (path, source) = locate(None, Ok(executable)).unwrap();
        assert_eq!(path, PathBuf::from("bin").join(LIBRARY_FILE_NAME));
        assert_eq!(source, Source::NextToExecutable);
        let (_, source) = locate(Some(OsString::new()), Ok(PathBuf::from("app"))).unwrap();
        assert_eq!(source, Source::NextToExecutable, "an empty variable is no path");
    }

    #[test]
    fn nowhere_else() {
        let error = locate(None, Err("gone".into())).unwrap_err();
        assert!(matches!(error, LoadError::NoExecutable { .. }));
        assert!(error.to_string().contains(LIBRARY_ENV));
    }

    #[test]
    fn a_missing_file_is_not_found_and_says_how_to_fix_it() {
        let path = std::env::temp_dir().join("am-no-such-folder").join(LIBRARY_FILE_NAME);
        let error = Library::load(&path).unwrap_err();
        assert!(
            matches!(
                error,
                LoadError::NotFound {
                    source: Source::Explicit,
                    ..
                }
            ),
            "{error}"
        );
        let error = Library::load_from(&path, Source::NextToExecutable).unwrap_err();
        assert!(error.to_string().contains(LIBRARY_ENV), "{error}");
    }

    #[test]
    fn the_abi_version_packs_major_and_minor() {
        assert_eq!(AM_V1_ABI_VERSION, 65537);
    }
}
