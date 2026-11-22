//! native/gate/stubs.py as a child process, as every runner starts it, and a small HTTP client for its control.

use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpStream;
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use serde_json::Value;

pub struct Stubs {
    child: Child,
    port: u16,
}

impl Stubs {
    /// Starts the stubs with `PYTHON` (else `python3`, or `python` on Windows) and reads their port.
    pub fn start(gate: &Path) -> Result<Stubs, String> {
        let python = std::env::var("PYTHON")
            .ok()
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| if cfg!(windows) { "python" } else { "python3" }.into());
        let mut child = Command::new(&python)
            .arg(gate.join("stubs.py"))
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()
            .map_err(|error| format!("cannot start {python} {}: {error}", gate.join("stubs.py").display()))?;
        let mut line = String::new();
        let stdout = child.stdout.take().ok_or("the stubs have no output")?;
        BufReader::new(stdout)
            .read_line(&mut line)
            .map_err(|error| error.to_string())?;
        let port = serde_json::from_str::<Value>(&line)
            .ok()
            .and_then(|document| document.get("port").and_then(Value::as_u64))
            .and_then(|port| u16::try_from(port).ok());
        match port {
            Some(port) => Ok(Stubs { child, port }),
            None => {
                let _ = child.kill();
                Err(format!("the stubs did not say their port: {line:?}"))
            }
        }
    }

    pub fn url(&self) -> String {
        format!("http://127.0.0.1:{}", self.port)
    }

    pub fn control(&self, body: &Value) -> Result<Value, String> {
        self.call("POST", "/_stub/control", Some(body))
    }

    pub fn seen(&self) -> Result<Value, String> {
        self.call("GET", "/_stub/seen", None)
    }

    fn call(&self, method: &str, path: &str, body: Option<&Value>) -> Result<Value, String> {
        let failed = |error: std::io::Error| format!("{method} {path} to the stubs: {error}");
        let mut stream = TcpStream::connect(("127.0.0.1", self.port)).map_err(failed)?;
        stream.set_read_timeout(Some(Duration::from_secs(10))).map_err(failed)?;
        stream
            .set_write_timeout(Some(Duration::from_secs(10)))
            .map_err(failed)?;
        let body = body.map(Value::to_string).unwrap_or_default();
        let request = format!(
            "{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{}\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\
             Connection: close\r\n\r\n{body}",
            self.port,
            body.len()
        );
        stream.write_all(request.as_bytes()).map_err(failed)?;
        let mut response = Vec::new();
        stream.read_to_end(&mut response).map_err(failed)?;
        let response = String::from_utf8_lossy(&response);
        let (head, body) = response
            .split_once("\r\n\r\n")
            .ok_or_else(|| format!("{method} {path}: no HTTP answer"))?;
        let status = head.split_whitespace().nth(1).unwrap_or_default();
        if status != "200" {
            return Err(format!("{method} {path}: the stubs answered {status}"));
        }
        serde_json::from_str(body).map_err(|error| format!("{method} {path}: {error}"))
    }

    /// Closes the stubs' input, which ends them; kills them if they are still there after 10 s.
    pub fn close(mut self) {
        drop(self.child.stdin.take());
        let deadline = Instant::now() + Duration::from_secs(10);
        while Instant::now() < deadline {
            if let Ok(Some(_)) = self.child.try_wait() {
                return;
            }
            std::thread::sleep(Duration::from_millis(20));
        }
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}
