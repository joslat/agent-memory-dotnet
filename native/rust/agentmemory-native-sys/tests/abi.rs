//! native/gate/REQUIRED.md items that pass the raw ABI what the safe crate never passes: null pointers.
//! Needs the library: AGENTMEMORY_NATIVE_LIBRARY, or the file next to the test executable.

use agentmemory_native_sys::*;
use std::ptr;

/// Tier A's configuration: a store nobody listens on, not verified at open, so no store is needed.
const CONFIG: &[u8] = br#"{"schemaVersion":1,"profile":"read_only","owner":{"mode":"fixed","id":"gate-owner-a"},"store":{"uri":"bolt://127.0.0.1:9","username":"neo4j","password":"unused","verifyConnectivity":false},"embedding":{"dimensions":8,"model":"gate-unit-axes"}}"#;

fn library() -> &'static Library {
    Library::load_default().unwrap_or_else(|error| panic!("{error}"))
}

#[test]
fn r_free_null_freeing_a_null_buffer_is_a_no_op() {
    let library = library();
    // SAFETY: the header says am_v1_buffer_free ignores NULL.
    unsafe { (library.am_v1_buffer_free)(ptr::null_mut()) };
}

#[test]
fn b15_state_with_a_null_output_is_refused_and_still_clears_the_others() {
    let library = library();
    let mut context: am_v1_context_t = 0;
    let mut error: *mut u8 = ptr::null_mut();
    let mut error_len: usize = 0;
    // SAFETY: the configuration is a valid slice for its length; the outputs are valid, writable locals.
    let status = unsafe {
        (library.am_v1_open)(
            AM_V1_ABI_VERSION,
            CONFIG.as_ptr(),
            CONFIG.len(),
            &mut context,
            &mut error,
            &mut error_len,
        )
    };
    // SAFETY: the buffer, if any, came from this library and is freed once; NULL is ignored.
    unsafe { (library.am_v1_buffer_free)(error.cast()) };
    assert_eq!(status, AM_V1_OK, "open");

    let mut values = [0xDEAD_u64; 4];
    let [a, b, c, d] = &mut values;
    // SAFETY: four valid, writable outputs and one NULL, which the library must refuse without writing through it.
    let status = unsafe { (library.am_v1_state)(context, a, b, c, d, ptr::null_mut()) };
    assert_eq!(status, AM_V1_INVALID_ARGUMENT);
    assert_eq!(values, [0, 0, 0, 0]);

    let mut status = AM_V1_BUSY;
    for _ in 0..1000 {
        // SAFETY: a handle this test opened; close takes no pointers.
        status = unsafe { (library.am_v1_close)(context) };
        if status != AM_V1_BUSY {
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(10));
    }
    assert_eq!(status, AM_V1_OK, "close");
}

#[test]
fn the_library_reports_abi_1_1_and_has_the_waiter() {
    let library = library();
    // SAFETY: takes nothing, returns a constant.
    let version = unsafe { (library.am_v1_abi_version)() };
    assert_eq!(version >> 16, AM_V1_ABI_MAJOR);
    assert!(library.has_wait(), "an ABI 1.1 library exports am_v1_wait");
}
