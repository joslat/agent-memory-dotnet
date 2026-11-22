//! The Rust runner of the shared native gate (native/gate/cases/FORMAT.md), on the binding's raw ABI
//! (`agentmemory_native::raw`).
//!
//! `cargo run --release --bin gate -- --tier A|B|C [--case ID] [--gate DIR]`. Environment as the gate's:
//! AGENTMEMORY_NATIVE_LIBRARY, AMP_URI, AMP_USER, AMP_PASSWORD, AMP_TLS_URI, AMP_TLS_OTHER_NAME_URI, GATE_TIER;
//! PYTHON for the stubs (else python3, or python on Windows).
#![forbid(unsafe_code)]

mod matching;
mod run;
mod stubs;
mod values;

use std::path::PathBuf;
use std::process::ExitCode;
use std::time::Instant;

use agentmemory_native::raw::Native;
use serde_json::Value;

use crate::stubs::Stubs;
use crate::values::Environment;

/// Why a case stopped before its end.
#[derive(Debug)]
pub enum Stop {
    /// It needs something this run does not have.
    Skip(String),
    /// A step's outcome did not match, or a step could not run.
    Failure(String),
}

fn read_json(path: &PathBuf) -> Result<Value, String> {
    let text = std::fs::read_to_string(path).map_err(|error| format!("cannot read {}: {error}", path.display()))?;
    serde_json::from_str(&text).map_err(|error| format!("{}: {error}", path.display()))
}

/// Every case of the tier, in file and list order. Tier B runs tier A's cases too, against the store.
fn load_cases(cases: &PathBuf, tier: &str) -> Result<Vec<Value>, String> {
    let mut files: Vec<PathBuf> = std::fs::read_dir(cases)
        .map_err(|error| format!("cannot read {}: {error}", cases.display()))?
        .filter_map(|entry| entry.ok().map(|entry| entry.path()))
        .filter(|path| {
            path.extension().is_some_and(|extension| extension == "json")
                && path.file_name().is_some_and(|name| name != "fixtures.json")
        })
        .collect();
    files.sort();
    let wanted: &[&str] = match tier {
        "A" => &["A"],
        "B" => &["A", "B"],
        "C" => &["C"],
        _ => &[],
    };
    let mut found = Vec::new();
    let mut ids = std::collections::HashSet::new();
    for file in files {
        let document = read_json(&file)?;
        for case in document["cases"].as_array().into_iter().flatten() {
            let id = case["id"].as_str().unwrap_or_default().to_owned();
            if !ids.insert(id.clone()) {
                return Err(format!("case id used twice: {id}"));
            }
            if wanted.contains(&case["tier"].as_str().unwrap_or_default()) {
                found.push(case.clone());
            }
        }
    }
    Ok(found)
}

fn option(args: &[String], name: &str) -> Option<String> {
    args.iter()
        .position(|arg| arg == name)
        .and_then(|at| args.get(at + 1))
        .cloned()
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let tier = option(&args, "--tier")
        .or_else(|| std::env::var("GATE_TIER").ok())
        .unwrap_or_else(|| "A".into())
        .to_uppercase();
    let only = option(&args, "--case");
    let gate = option(&args, "--gate")
        .or_else(|| std::env::var("AGENTMEMORY_GATE_DIR").ok())
        .map_or_else(
            || {
                PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                    .join("..")
                    .join("..")
                    .join("gate")
            },
            PathBuf::from,
        );

    let native = match Native::load() {
        Ok(native) => native,
        Err(error) => {
            eprintln!("{error}");
            return ExitCode::from(2);
        }
    };
    let loaded = load_cases(&gate.join("cases"), &tier)
        .and_then(|cases| read_json(&gate.join("cases").join("fixtures.json")).map(|fixtures| (cases, fixtures)));
    let (cases, fixtures) = match loaded {
        Ok(loaded) => loaded,
        Err(error) => {
            eprintln!("{error}");
            return ExitCode::from(2);
        }
    };
    let stubs = match Stubs::start(&gate) {
        Ok(stubs) => Some(stubs),
        Err(error) => {
            eprintln!("warning: {error}; the cases that need the stubs are skipped");
            None
        }
    };
    let env = Environment::new(&tier, stubs.as_ref().map(Stubs::url));

    let (mut passed, mut failed, mut skipped) = (0, 0, 0);
    for case in cases
        .iter()
        .filter(|case| only.as_deref().is_none_or(|only| case["id"] == only))
    {
        let id = case["id"].as_str().unwrap_or("?");
        let started = Instant::now();
        match run::run_case(native, case, &env, &fixtures, stubs.as_ref()) {
            Ok(()) => {
                passed += 1;
                println!("ok      {id} ({} ms)", started.elapsed().as_millis());
            }
            Err(Stop::Skip(reason)) => {
                skipped += 1;
                println!("skipped {id}: {reason}");
            }
            Err(Stop::Failure(reason)) => {
                failed += 1;
                println!("FAIL    {id}: {reason}");
            }
        }
    }
    if let Some(stubs) = stubs {
        stubs.close();
    }
    println!(
        "tier {tier}: {passed} passed, {failed} failed, {skipped} skipped (Rust, rustc {})",
        env!("GATE_RUSTC_VERSION")
    );
    if failed > 0 {
        ExitCode::FAILURE
    } else {
        ExitCode::SUCCESS
    }
}
