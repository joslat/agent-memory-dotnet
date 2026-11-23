//! R-NEVER-UNLOAD (native/gate/REQUIRED.md): the library stays loaded until the process ends, whatever else closes a
//! handle to it. Its .NET runtime has threads of its own that outlive a close: in Node, koffi unloaded it under them as
//! the process exited, and the process crashed (NATIVE-TS-EXIT-ARM). The binding holds its own handle and never closes it.
//! Needs the library: AGENTMEMORY_NATIVE_LIBRARY, or the file next to the test executable.

use agentmemory_native_sys::Library;
use std::path::{Path, PathBuf};

/// A small system library to copy, and a look at whether a file is loaded in this process.
struct Probe {
    source: PathBuf,
    extension: &'static str,
}

impl Probe {
    fn find() -> Option<Probe> {
        if cfg!(windows) {
            let root = std::env::var_os("SystemRoot").unwrap_or_else(|| "C:\\Windows".into());
            let source = Path::new(&root).join("System32").join("version.dll");
            return source.is_file().then_some(Probe {
                source,
                extension: ".dll",
            });
        }
        if cfg!(target_os = "linux") {
            let triplet = if cfg!(target_arch = "aarch64") {
                "aarch64-linux-gnu"
            } else {
                "x86_64-linux-gnu"
            };
            let folders = [
                format!("/lib/{triplet}"),
                format!("/usr/lib/{triplet}"),
                "/lib64".into(),
                "/usr/lib64".into(),
            ];
            return folders
                .iter()
                .map(|folder| Path::new(folder).join("libz.so.1"))
                .find(|path| path.is_file())
                .map(|source| Probe {
                    source,
                    extension: ".so",
                });
        }
        None // macOS keeps its system libraries in the dyld cache, not as files to copy
    }

    #[cfg(windows)]
    fn is_loaded(&self, path: &Path) -> bool {
        // GetModuleHandleExW: finds a module already in the process and takes a reference, which the drop gives back.
        libloading::os::windows::Library::open_already_loaded(path).is_ok()
    }

    #[cfg(not(windows))]
    fn is_loaded(&self, path: &Path) -> bool {
        let path = std::fs::canonicalize(path).unwrap_or_else(|_| path.to_path_buf());
        std::fs::read_to_string("/proc/self/maps")
            .map(|maps| maps.contains(path.to_string_lossy().as_ref()))
            .unwrap_or(false)
    }
}

fn scratch() -> PathBuf {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_nanos());
    let folder = std::env::temp_dir().join(format!("am-rust-unload-{}-{nanos}", std::process::id()));
    std::fs::create_dir_all(&folder).unwrap();
    folder
}

#[test]
fn r_never_unload_the_binding_keeps_the_library_loaded_after_every_other_handle_is_closed() {
    let Some(probe) = Probe::find() else {
        eprintln!(
            "R-NEVER-UNLOAD: no small system library to copy and no cheap look at what is loaded on this platform"
        );
        return;
    };

    // The control: a library opened and closed through libloading is gone again, else the check below proves nothing.
    let folder = scratch();
    let control = folder.join(format!("control{}", probe.extension));
    std::fs::copy(&probe.source, &control).unwrap();
    // SAFETY: a copy of a small system library; its initialisers are the system's own.
    let handle = unsafe { libloading::Library::new(&control) }.unwrap();
    assert!(probe.is_loaded(&control), "the control copy is loaded");
    drop(handle);
    assert!(
        !probe.is_loaded(&control),
        "closing the only handle unloads a library (else this test proves nothing)"
    );

    // The binding: its library stays loaded after another handle to the same file is opened and closed.
    let library = Library::load_default().unwrap_or_else(|error| panic!("{error}"));
    // SAFETY: the library is loaded already; this only takes and gives back one more reference to it.
    let other = unsafe { libloading::Library::new(library.path()) }.unwrap();
    drop(other);
    assert!(
        probe.is_loaded(library.path()),
        "the binding's own handle keeps {} loaded after every other handle is closed",
        library.path().display()
    );
    let _ = std::fs::remove_dir_all(folder);
}
