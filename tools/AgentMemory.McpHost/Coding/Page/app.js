"use strict";
(() => {
  const $ = id => document.getElementById(id);
  const project = location.pathname.split("/")[2];
  const base = `/projects/${encodeURIComponent(project)}/`;
  let token = "", generation = 0, selected = null, jobId = null, attempt = null;
  const requests = new Set();
  $("project").textContent = `Project / ${project}`;
  const notice = text => { $("notice").textContent = text; };

  async function api(operation, body) {
    const current = generation, controller = new AbortController();
    requests.add(controller);
    const timeout = setTimeout(() => controller.abort(), 15000);
    try {
      const response = await fetch(base + operation, {
        method: "POST", headers: { "Authorization": `Bearer ${token}`, "Content-Type": "application/json" },
        body: JSON.stringify(body), signal: controller.signal, cache: "no-store", credentials: "omit", redirect: "error"
      });
      if (current !== generation) throw new Error("Disconnected.");
      if (!response.ok) throw new Error(response.status === 401 ? "Project token was refused." :
        response.status === 404 ? "No retained job status. Check history before trying another correction." :
        response.status === 400 ? "Input was refused. Check length, source and secret content." :
        "The service could not complete this request.");
      const value = await response.json();
      if (current !== generation) throw new Error("Disconnected.");
      return value;
    } finally { clearTimeout(timeout); requests.delete(controller); }
  }

  function disconnect() {
    generation++;
    for (const request of requests) request.abort();
    token = ""; selected = jobId = attempt = null;
    $("token").value = $("correction").value = $("commit").value = $("memory-id").value = "";
    $("memories").replaceChildren();
    for (const id of ["profile", "selected", "job-status"]) $(id).textContent = "";
    $("workspace").hidden = $("correction-panel").hidden = $("job-panel").hidden = true;
    $("access").hidden = false;
    notice("Disconnected. Tab credentials and displayed memories cleared.");
  }

  async function action(button, work) {
    const current = generation;
    button.disabled = true;
    try { await work(); }
    catch (error) { if (current === generation) notice(error.name === "AbortError" ? "Request timed out. Check history before retrying a write." : error.message); }
    finally { button.disabled = false; }
  }

  function element(tag, text, className) {
    const node = document.createElement(tag);
    node.textContent = text;
    if (className) node.className = className;
    return node;
  }

  function render(rows) {
    $("memories").replaceChildren();
    $("count").textContent = `${rows.length} shown`;
    if (!rows.length) $("memories").append(element("p", "No memories found in this view.", "hint"));
    for (const row of rows) {
      const article = element("article", "", "memory");
      article.append(element("span", `${row.kind} · ${row.status}`, `badge ${row.status === "live" ? "" : "closed"}`));
      article.append(element("p", row.summary || "(No summary)", "summary"));
      const details = element("details", "", "details");
      details.append(element("summary", "Source and history"));
      // Stored values are text throughout. They never become HTML, selectors or navigation targets.
      details.append(element("pre", JSON.stringify({ id: row.id, owner: row.ownerId,
        created: row.createdAtUtc, updated: row.updatedAtUtc, closed: row.invalidatedAtUtc,
        closedAs: row.closedAs, sourceMessageIds: row.sourceMessageIds,
        supersededByIds: row.supersededByIds, supersedesIds: row.supersedesIds, metadata: row.metadata }, null, 2)));
      article.append(details);
      if (row.status === "live") {
        const actions = element("div", "", "actions");
        const correct = element("button", "Correct", "secondary");
        correct.addEventListener("click", () => {
          selected = row;
          $("selected").textContent = row.summary;
          $("correction").value = "";
          $("correction-panel").hidden = false;
          $("correction").focus();
        });
        const close = element("button", "Close memory", "close");
        close.addEventListener("click", () => {
          if (!confirm("Close this memory? It will stop being live; its history remains.")) return;
          void action(close, async () => {
            const result = await api("forget", { kind: row.kind, id: row.id });
            notice(result.invalidated ? "Memory closed. Its history is retained." : "No live memory was closed. Refresh to check its current state.");
            if (selected?.id === row.id) { selected = null; $("correction-panel").hidden = true; }
            await history();
          });
        });
        actions.append(correct, close); article.append(actions);
      }
      $("memories").append(article);
    }
  }

  async function history() {
    const id = $("memory-id").value.trim();
    render(await api("history", id ? { id } : {}));
  }
  async function refresh() {
    const result = await api("profile", {});
    $("profile").textContent = result.profile?.text || "No working profile yet.";
    await history();
  }

  $("connect-form").addEventListener("submit", event => {
    event.preventDefault();
    void action(event.submitter, async () => {
      token = $("token").value; $("token").value = "";
      try {
        await refresh();
        $("workspace").hidden = false; $("access").hidden = true;
        notice("Connected. Viewing this project's owner-scoped memories.");
      } catch (error) { disconnect(); notice(error.message); }
    });
  });
  $("disconnect").addEventListener("click", disconnect);
  window.addEventListener("pagehide", disconnect);
  $("refresh").addEventListener("click", event => void action(event.target, refresh));
  $("lookup-form").addEventListener("submit", event => {
    event.preventDefault(); void action(event.submitter, history);
  });
  $("correction-form").addEventListener("submit", event => {
    event.preventDefault();
    if (!selected) return;
    void action(event.submitter, async () => {
      const userText = `Correction to ${selected.kind} memory ${selected.id}. Previous summary: ${selected.summary.slice(0, 1000)}\nCorrect information: ${$("correction").value.trim()}`;
      const payload = { userText, commit: $("commit").value.trim(), source: `memory-page/${selected.kind}/${selected.id}` };
      const fingerprint = JSON.stringify(payload);
      // A repeated submission of identical text reuses its turn even after an ambiguous network failure.
      if (attempt?.fingerprint !== fingerprint) attempt = { fingerprint, body: { ...payload, session: "memory-page", turnId: crypto.randomUUID() } };
      jobId = null;
      $("job-panel").hidden = true;
      $("job-status").textContent = "";
      notice("Submitting correction. Acceptance is not completion.");
      let job;
      try { job = await api("remember", attempt.body); }
      catch { throw new Error("Correction receipt unavailable. Completion is unknown; inspect history before retrying."); }
      jobId = job.jobId;
      $("job-panel").hidden = false;
      showJob(job);
      notice("Correction accepted. Check its status, then refresh history to review the result.");
    });
  });

  function showJob(job) {
    const explanations = {
      queued: "Waiting for the writer.", running: "The writer is processing this correction.",
      succeeded: "Processing finished. Review history; this does not guarantee that the proposed correction was applied.",
      partial: "Only part of the write completed. Inspect history before another submission.",
      failed: "The write failed; some source data may remain. Inspect history.",
      unknown: "Completion is unknown. Inspect history before another submission.",
      cancelled: "The waiting write was cancelled."
    };
    $("job-status").textContent = `${job.state}: ${explanations[job.state] || "Unrecognized job state; inspect history."} Job ${job.jobId}`;
  }
  $("check-job").addEventListener("click", event => void action(event.target, async () => {
    if (jobId) showJob(await api("write_status", { jobId }));
  }));
})();
