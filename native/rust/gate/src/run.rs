//! One case: its contexts and jobs by name, its variables, and every step of FORMAT.md on the raw ABI.

use std::time::{Duration, Instant};

use agentmemory_native::raw::{self, Native, ABI_VERSION};
use agentmemory_native::sys;
use serde_json::{json, Map, Value};

use crate::matching::{check, matches};
use crate::stubs::Stubs;
use crate::values::{fresh_state, random_u64, raw_bytes, resolve, Environment};
use crate::Stop;

/// The keys of a step that are not its action.
const NOT_ACTIONS: [&str; 4] = ["expect", "as", "note", "save"];

enum Jobs {
    One(u64, u64),
    Many(Vec<(u64, u64)>),
}

impl Jobs {
    fn pairs(&self) -> Vec<(u64, u64)> {
        match self {
            Jobs::One(context, job) => vec![(*context, *job)],
            Jobs::Many(pairs) => pairs.clone(),
        }
    }
}

struct Run<'a> {
    native: Native,
    env: &'a Environment,
    fixtures: &'a Value,
    stubs: Option<&'a Stubs>,
    contexts: Vec<(String, u64)>,
    jobs: Vec<(String, Jobs)>,
    vars: Map<String, Value>,
}

fn failure(text: impl Into<String>) -> Stop {
    Stop::Failure(text.into())
}

fn name(status: i32) -> Value {
    Value::String(raw::status_name(status))
}

fn action(step: &Map<String, Value>) -> Result<&str, Stop> {
    step.keys()
        .map(String::as_str)
        .find(|key| !NOT_ACTIONS.contains(key))
        .ok_or_else(|| failure("a step without an action"))
}

fn text<'v>(args: &'v Map<String, Value>, key: &str, otherwise: &'v str) -> &'v str {
    args.get(key).and_then(Value::as_str).unwrap_or(otherwise)
}

fn number(args: &Map<String, Value>, key: &str, otherwise: f64) -> f64 {
    args.get(key).and_then(Value::as_f64).unwrap_or(otherwise)
}

fn millis(args: &Map<String, Value>, key: &str, otherwise: u32) -> u32 {
    args.get(key)
        .and_then(Value::as_u64)
        .map_or(otherwise, |value| u32::try_from(value).unwrap_or(u32::MAX))
}

fn seconds(value: f64) -> Duration {
    Duration::from_secs_f64(value.max(0.0))
}

impl<'a> Run<'a> {
    fn resolve(&self, value: &Value) -> Result<Value, Stop> {
        resolve(value, self.env, self.fixtures, &self.vars)
    }

    fn raw_bytes(&self, spec: &Value) -> Result<Vec<u8>, Stop> {
        raw_bytes(spec, self.env, self.fixtures, &self.vars)
    }

    fn set_context(&mut self, name: &str, handle: u64) {
        self.contexts.retain(|(existing, _)| existing != name);
        self.contexts.push((name.to_owned(), handle));
    }

    fn set_jobs(&mut self, name: &str, jobs: Jobs) {
        self.jobs.retain(|(existing, _)| existing != name);
        self.jobs.push((name.to_owned(), jobs));
    }

    fn named_jobs(&self, name: &str) -> Vec<(u64, u64)> {
        self.jobs
            .iter()
            .find(|(existing, _)| existing == name)
            .map(|(_, jobs)| jobs.pairs())
            .unwrap_or_default()
    }

    fn context(&self, args: &Map<String, Value>) -> Result<u64, Stop> {
        let given = args.get("ctx").cloned().unwrap_or_else(|| json!("ctx"));
        match self.resolve(&given)? {
            Value::Number(handle) => handle
                .as_u64()
                .ok_or_else(|| failure("a context handle is an unsigned number")),
            Value::String(name) => self
                .contexts
                .iter()
                .find(|(existing, _)| *existing == name)
                .map(|(_, handle)| *handle)
                .ok_or_else(|| failure(format!("no context named {name}"))),
            other => Err(failure(format!("ctx {other} is neither a name nor a handle"))),
        }
    }

    fn job(&self, args: &Map<String, Value>) -> Result<(u64, u64), Stop> {
        let name = text(args, "job", "job");
        match self.jobs.iter().find(|(existing, _)| existing == name) {
            Some((_, Jobs::One(context, job))) => Ok((*context, *job)),
            _ => Err(failure(format!("no job named {name}"))),
        }
    }

    fn request_bytes(&self, args: &Map<String, Value>) -> Result<Vec<u8>, Stop> {
        match args.get("raw") {
            Some(raw) => self.raw_bytes(raw),
            None => {
                let request = self.resolve(args.get("request").unwrap_or(&Value::Null))?;
                serde_json::to_vec(&request).map_err(|error| failure(error.to_string()))
            }
        }
    }

    fn submit(&self, args: &Map<String, Value>) -> Result<(i32, u64), Stop> {
        let data = self.request_bytes(args)?;
        Ok(self
            .native
            .submit(self.context(args)?, &data, millis(args, "timeoutMs", 0)))
    }

    fn poll(&self, context: u64, job: u64, wait_ms: u32) -> Result<(i32, Value), Stop> {
        let output = self.native.poll(context, job, wait_ms);
        match output.result {
            Some(bytes) if output.status == sys::AM_V1_OK && output.disposition == sys::AM_V1_TERMINAL => {
                let result = serde_json::from_slice(&bytes)
                    .map_err(|error| failure(format!("a result that is not JSON: {error}")))?;
                Ok((output.status, result))
            }
            _ => Ok((output.status, Value::Null)),
        }
    }

    fn await_result(&self, context: u64, job: u64, budget: Duration) -> Result<Value, Stop> {
        let deadline = Instant::now() + budget;
        loop {
            let (status, result) = self.poll(context, job, 100)?;
            if status != sys::AM_V1_OK {
                return Err(failure(format!(
                    "am_v1_poll returned {} while waiting",
                    raw::status_name(status)
                )));
            }
            if !result.is_null() {
                return Ok(result);
            }
            if Instant::now() > deadline {
                return Err(failure(format!("no result within {} s", budget.as_secs())));
            }
        }
    }

    fn close(&self, context: u64, wait: Duration) -> i32 {
        let deadline = Instant::now() + wait;
        loop {
            let status = self.native.close(context);
            if status != sys::AM_V1_BUSY || Instant::now() > deadline {
                return status;
            }
            std::thread::sleep(Duration::from_millis(10));
        }
    }

    fn step(&mut self, step: &Value) -> Result<Value, Stop> {
        let object = step.as_object().ok_or_else(|| failure("a step is an object"))?;
        let action = action(object)?;
        let mut args = match &object[action] {
            Value::Object(args) => args.clone(),
            _ => Map::new(),
        };
        if let Some(name) = object.get("as") {
            args.insert("as".into(), name.clone());
        }
        let outcome = self.act(action, &args)?;
        if let Some(Value::String(name)) = object.get("save") {
            self.vars.insert(name.clone(), outcome.clone());
        }
        Ok(outcome)
    }

    fn act(&mut self, action: &str, args: &Map<String, Value>) -> Result<Value, Stop> {
        let native = self.native;
        Ok(match action {
            "abi" => json!({"version": native.abi_version(), "hasWait": native.has_wait()}),
            "open" => {
                let data = match args.get("raw") {
                    Some(raw) => self.raw_bytes(raw)?,
                    None => serde_json::to_vec(&self.resolve(args.get("config").unwrap_or(&Value::Null))?)
                        .map_err(|error| failure(error.to_string()))?,
                };
                let abi = millis(args, "abiVersion", ABI_VERSION);
                let opened = native.open(abi, &data);
                let mut outcome = json!({"status": name(opened.status)});
                if opened.status == sys::AM_V1_OK {
                    self.set_context(text(args, "as", "ctx"), opened.context);
                    outcome["handle"] = json!(opened.context);
                } else if let Some(document) = opened.error {
                    let document: Value = serde_json::from_slice(&document)
                        .map_err(|error| failure(format!("an error document that is not JSON: {error}")))?;
                    let error = document.get("error").cloned().unwrap_or_else(|| json!({}));
                    outcome["code"] = error.get("code").cloned().unwrap_or(Value::Null);
                    outcome["message"] = error.get("message").cloned().unwrap_or_else(|| json!(""));
                }
                outcome
            }
            "close" => {
                let status = self.close(self.context(args)?, seconds(number(args, "waitS", 10.0)));
                if status == sys::AM_V1_OK {
                    match args.get("ctx") {
                        None => self.contexts.retain(|(existing, _)| existing != "ctx"),
                        Some(Value::String(given)) => self.contexts.retain(|(existing, _)| existing != given),
                        Some(_) => {}
                    }
                }
                json!({"status": name(status)})
            }
            "submit" => {
                let (status, job) = self.submit(args)?;
                if status == sys::AM_V1_OK {
                    let context = self.context(args)?;
                    self.set_jobs(text(args, "as", "job"), Jobs::One(context, job));
                }
                json!({"status": name(status)})
            }
            "await" => {
                let (context, job) = self.job(args)?;
                self.await_result(context, job, seconds(number(args, "budgetS", 30.0)))?
            }
            "call" => {
                let (status, job) = self.submit(args)?;
                if status != sys::AM_V1_OK {
                    return Err(failure(format!("am_v1_submit returned {}", raw::status_name(status))));
                }
                let context = self.context(args)?;
                let result = self.await_result(context, job, seconds(number(args, "budgetS", 30.0)));
                native.job_release(context, job);
                result?
            }
            "poll" => {
                let (context, job) = self.job(args)?;
                let (status, result) = self.poll(context, job, millis(args, "waitMs", 0))?;
                json!({"status": name(status), "result": result})
            }
            "cancel" => {
                let (context, job) = match args.get("handle") {
                    Some(handle) => (
                        self.context(args)?,
                        handle
                            .as_u64()
                            .ok_or_else(|| failure("a job handle is an unsigned number"))?,
                    ),
                    None => self.job(args)?,
                };
                json!({"status": name(native.cancel(context, job))})
            }
            "release" => {
                let (context, job) = self.job(args)?;
                json!({"status": name(native.job_release(context, job))})
            }
            "state" => {
                let state = native.state(self.context(args)?);
                let flag = |bit: u32| state.flags & bit != 0;
                json!({
                    "status": name(state.status), "stateRevision": state.state_revision,
                    "policyEpoch": state.policy_epoch, "deletionEpoch": state.deletion_epoch,
                    "memoryRevision": state.memory_revision, "readAllowed": flag(sys::AM_V1_READ_ALLOWED),
                    "admissionAllowed": flag(sys::AM_V1_ADMISSION_ALLOWED),
                    "writeQuiescent": flag(sys::AM_V1_WRITE_QUIESCENT), "closing": flag(sys::AM_V1_CLOSING_FLAG),
                    "erasePending": flag(sys::AM_V1_ERASE_PENDING),
                })
            }
            "submitMany" => {
                let mut admitted = Vec::new();
                let mut refused = Map::new();
                for _ in 0..args.get("count").and_then(Value::as_u64).unwrap_or(0) {
                    let (status, job) = self.submit(args)?;
                    if status == sys::AM_V1_OK {
                        admitted.push((self.context(args)?, job));
                    } else {
                        let count = refused.entry(raw::status_name(status)).or_insert(json!(0));
                        *count = json!(count.as_u64().unwrap_or(0) + 1);
                    }
                }
                let count = admitted.len();
                self.set_jobs(text(args, "as", "jobs"), Jobs::Many(admitted));
                json!({"admitted": count, "refused": refused})
            }
            "awaitAll" => {
                let name = text(args, "jobs", "jobs").to_owned();
                let budget = seconds(number(args, "budgetS", 30.0));
                let mut results = Vec::new();
                let pairs = self.named_jobs(&name);
                self.set_jobs(&name, Jobs::Many(Vec::new()));
                for (i, (context, job)) in pairs.iter().enumerate() {
                    let result = self.await_result(*context, *job, budget);
                    native.job_release(*context, *job);
                    if result.is_err() {
                        // Release the rest too: the case has failed.
                        pairs[i + 1..].iter().for_each(|(c, j)| {
                            native.job_release(*c, *j);
                        });
                    }
                    results.push(result?);
                }
                json!({"results": results})
            }
            "drain" => {
                let context = self.context(args)?;
                let count = args.get("count").and_then(Value::as_u64).unwrap_or(0) as usize;
                let deadline = Instant::now() + seconds(number(args, "budgetS", 30.0));
                let mut reported = Vec::new();
                while reported.len() < count && Instant::now() < deadline {
                    let (status, job) = native
                        .wait(context, millis(args, "waitMs", 1000))
                        .ok_or_else(|| failure("the library has no am_v1_wait (ABI 1.0)"))?;
                    if status != sys::AM_V1_OK {
                        return Err(failure(format!("am_v1_wait returned {}", raw::status_name(status))));
                    }
                    if job != 0 {
                        reported.push(job);
                    }
                }
                let mut submitted: Vec<u64> = self
                    .named_jobs(text(args, "jobs", "jobs"))
                    .into_iter()
                    .map(|(_, job)| job)
                    .collect();
                submitted.sort_unstable();
                let mut results = Vec::new();
                if args
                    .get("takeResults")
                    .is_some_and(|take| take.as_bool().unwrap_or(false))
                {
                    for job in &reported {
                        results.push(self.poll(context, *job, 0)?.1);
                    }
                }
                let count = reported.len();
                reported.sort_unstable();
                json!({"reported": count, "sameAsSubmitted": reported == submitted, "results": results})
            }
            "waitAny" => {
                let (status, job) = native
                    .wait(self.context(args)?, millis(args, "waitMs", 0))
                    .ok_or_else(|| failure("the library has no am_v1_wait (ABI 1.0)"))?;
                json!({"status": name(status), "job": job})
            }
            "releaseAll" => {
                let name = text(args, "jobs", "jobs").to_owned();
                for (context, job) in self.named_jobs(&name) {
                    native.job_release(context, job);
                }
                self.set_jobs(&name, Jobs::Many(Vec::new()));
                json!({})
            }
            "repeat" => {
                let step = args.get("step").cloned().unwrap_or(Value::Null);
                let times = args.get("times").and_then(Value::as_u64).unwrap_or(0);
                let pause = seconds(number(args, "sleepS", 0.0));
                let mut outcome = Value::Null;
                for attempt in 0..times {
                    if attempt > 0 && !pause.is_zero() {
                        std::thread::sleep(pause);
                    }
                    outcome = self.step(&step)?;
                    if let Some(until) = args.get("until") {
                        if matches(&outcome, &self.resolve(until)?) {
                            break;
                        }
                    }
                }
                outcome
            }
            "stub" => {
                let stubs = self.stubs.ok_or_else(|| Stop::Skip("no stubs in this run".into()))?;
                let answer = if args.get("seen").is_some_and(|seen| seen.as_bool().unwrap_or(false)) {
                    stubs.seen()
                } else {
                    stubs.control(&Value::Object(args.clone()))
                };
                answer.map_err(failure)?
            }
            "set" => {
                for (key, value) in args {
                    let value = match value.get("$unique").and_then(Value::as_str) {
                        Some(prefix) => json!(format!("{prefix}{:016x}", random_u64())),
                        None => self.resolve(value)?,
                    };
                    self.vars.insert(key.clone(), value);
                }
                json!({})
            }
            "sleep" => {
                std::thread::sleep(seconds(number(args, "seconds", 0.0)));
                json!({})
            }
            other => return Err(failure(format!("unknown step {other}"))),
        })
    }

    /// Releases what the case still holds and closes its contexts, after a held write is let go.
    fn finish(&mut self, uses_stubs: bool) {
        if let (Some(stubs), true) = (self.stubs, uses_stubs) {
            let _ = stubs.control(&json!({"mode": "ok", "release": true}));
        }
        for (_, jobs) in std::mem::take(&mut self.jobs) {
            for (context, job) in jobs.pairs() {
                self.native.job_release(context, job);
            }
        }
        for (_, context) in std::mem::take(&mut self.contexts) {
            self.close(context, Duration::from_secs(30));
        }
    }
}

/// Runs one case: `Ok`, or `Stop::Skip`, or `Stop::Failure` naming the step that failed.
pub fn run_case(
    native: Native,
    case: &Value,
    env: &Environment,
    fixtures: &Value,
    stubs: Option<&Stubs>,
) -> Result<(), Stop> {
    let id = case["id"].as_str().unwrap_or("?");
    for need in case.get("needs").and_then(Value::as_array).into_iter().flatten() {
        let need = need.as_str().unwrap_or_default();
        if env.vars.get(need).is_none_or(Value::is_null) {
            return Err(Stop::Skip(format!("needs {need}")));
        }
    }
    let uses_stubs = case.get("stubs").and_then(Value::as_bool).unwrap_or(false);
    let mut run = Run {
        native,
        env,
        fixtures,
        stubs,
        contexts: Vec::new(),
        jobs: Vec::new(),
        vars: Map::new(),
    };
    run.vars.insert("stateDir".into(), Value::String(fresh_state()?));
    let result = (|| -> Result<(), Stop> {
        if let (Some(stubs), true) = (stubs, uses_stubs) {
            stubs.control(&json!({"reset": true})).map_err(failure)?;
        }
        for (index, step) in case["steps"].as_array().into_iter().flatten().enumerate() {
            let outcome = run.step(step)?;
            if let Some(expect) = step.get("expect") {
                let expected = run.resolve(expect)?;
                if let Err(text) = check(&outcome, &expected, "") {
                    let action = step.as_object().map_or(Ok("?"), action)?;
                    return Err(failure(format!("{id}, step {} ({action}): {text}", index + 1)));
                }
            }
        }
        Ok(())
    })();
    run.finish(uses_stubs);
    result
}
