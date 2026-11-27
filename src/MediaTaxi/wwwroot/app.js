import { byId, getJson, element, badge, showError, formatDate, connectEvents, setProcessingNotice } from "/common.js";

let currentView = "active";
let timeZone;
let loading = false;
let reloadRequested = false;
let reloadTimer;
let configuredDestinations = [];

function stageText(job) {
  if (job.stage === "Done" || job.hiresPhase === "Complete") {
    const succeeded = job.destinations.filter((item) => item.copyStatus === "Succeeded").length;
    if (job.destinations.length && succeeded === 0) return ["Delivery failed", "failure"];
    if (succeeded < job.destinations.length) return ["Done with warnings", "warning"];
    if (job.destinations.some((item) => !item.inventoryConfirmed)) return ["Copied; awaiting Tria", "working"];
    if (job.proxyStatus === "Failed") return ["Done with warnings", "warning"];
    return ["Done", "success"];
  }
  if (job.stage === "Existing") return [job.location === "Failed" ? "Existing failure" : "Existing", job.location === "Failed" ? "failure" : "neutral"];
  if (job.stage === "Failed" || job.stage === "MoveFailed") return [job.error?.toLowerCase().includes("format") ? "Format mismatch" : job.stage === "MoveFailed" ? "Move failed" : "Failed", "failure"];
  const labels = { Waiting: "Waiting for copy", Queued: "Pending", Probing: "Probing", Wrapping: "Native rewrap", Transcoding: "Transcoding", Delivering: "Delivering", Archiving: "Archiving", Proxy: "Generating proxy", CleanupPending: "Cleanup pending" };
  return [labels[job.stage] || job.stage, job.stage === "CleanupPending" ? "warning" : "working"];
}

function renderDestination(item) {
  const cell = element("td", null, "destination-cell");
  if (!item) { cell.append(badge("Unknown", "warning")); return cell; }
  const tones = { OK: "success", Missing: "failure", Removed: "neutral", Unknown: "warning" };
  cell.append(badge(item.availability, tones[item.availability] || "neutral"));
  if (item.copyStatus === "InProgress") cell.append(element("small", "Copying", "copy-status"));
  else if (item.copyStatus === "Failed") cell.append(element("small", "Copy failed", "copy-status failure-text"));
  else if (item.copyStatus === "Succeeded") cell.append(element("small", item.inventoryConfirmed ? "OGP presence confirmed" : "Copied; awaiting OGP", "copy-status"));
  if (item.error) cell.title = item.error;
  return cell;
}

function renderJob(job, destinations) {
  const row = element("tr");
  const arrived = element("td", formatDate(job.arrivalUtc, timeZone), "date-cell");
  arrived.title = job.arrivalUtc;
  const name = element("td", null, "name-cell");
  name.append(element("strong", job.clipName || job.originalName), element("small", `${job.originalName} · ${job.location}`));
  const format = element("td", null, "format-cell");
  format.append(element("span", job.mediaSummary || "Waiting for media probe"));
  if (job.mode && job.mode !== "Unknown") format.append(element("small", job.mode));
  const status = element("td", null, "status-cell");
  const [label, tone] = stageText(job);
  status.append(badge(label, tone));
  if (job.cacheWaitReason) status.append(element("small", job.cacheWaitReason, "row-error"));
  if (job.progress !== null && job.progress !== undefined && ["Transcoding", "Proxy", "Delivering", "Wrapping"].includes(job.stage)) {
    const percentage = Math.min(100, Math.max(0, Number(job.progress)));
    const progress = element("progress");
    progress.max = 100;
    progress.value = percentage;
    progress.setAttribute("aria-label", `${label}: ${Math.round(percentage)} percent`);
    status.append(element("div", null, "progress-container"));
    status.lastChild.append(progress, element("small", `${Math.round(percentage)}%`));
  }
  if (job.error) { const error = element("small", job.error, "row-error"); error.title = job.error; status.append(error); }
  const proxy = element("td");
  const proxyTone = job.proxyStatus === "Ready" ? "success" : job.proxyStatus === "Failed" ? "failure" : ["Pending", "Not required", "Unknown", "Untracked"].includes(job.proxyStatus) ? "neutral" : "working";
  proxy.append(badge(job.proxyStatus || "Pending", proxyTone));
  if (job.proxyDeferralReason) proxy.append(element("small", job.proxyDeferralReason));
  if (job.proxyStatus === "Working" && job.proxyProgress != null) {
    const meter = element("progress"); meter.max = 100; meter.value = Math.min(100, Math.max(0, job.proxyProgress));
    meter.setAttribute("aria-label", `Proxy: ${Math.round(meter.value)} percent`); proxy.append(meter);
  }
  row.append(arrived, name, format, status, proxy);
  for (const destination of destinations) row.append(renderDestination(job.destinations.find((item) => item.id === destination.id)));
  return row;
}

function render(snapshot) {
  timeZone = snapshot.timeZone;
  byId("active-count").textContent = snapshot.activeCount;
  byId("older-count").textContent = snapshot.olderCount;
  const destinations = new Map(configuredDestinations.map((item) => [item.id, item]));
  for (const job of snapshot.jobs) for (const item of job.destinations) if (!destinations.has(item.id)) destinations.set(item.id, item);
  const columns = [...destinations.values()];
  const heading = element("tr");
  for (const label of ["Arrived", "Clip", "Input format", "Processing", "Proxy", ...columns.map((item) => item.name)]) {
    const cell = element("th", label); cell.scope = "col"; heading.append(cell);
  }
  byId("activity-head").replaceChildren(heading);
  const jobs = [...snapshot.jobs].sort((a, b) => new Date(b.arrivalUtc) - new Date(a.arrivalUtc));
  byId("activity-body").replaceChildren(...jobs.map((job) => renderJob(job, columns)));
  byId("empty-state").classList.toggle("hidden", jobs.length > 0);
  byId("empty-state").firstElementChild.textContent = currentView === "active" ? "Waiting for incoming media" : "No older media to monitor";
  byId("empty-state").lastElementChild.textContent = currentView === "active" ? "The last hour and unresolved Watch jobs appear here." : "Entries stay here while their source remains in Finished or Failed.";
  byId("row-count").textContent = `${jobs.length} ${jobs.length === 1 ? "clip" : "clips"}`;
  byId("updated").textContent = `Updated ${formatDate(new Date().toISOString(), timeZone)}`;
}

async function refreshJobs() {
  if (loading) { reloadRequested = true; return; }
  loading = true;
  const requestedView = currentView;
  try {
    const snapshot = await getJson(`/api/jobs?view=${requestedView}`);
    if (requestedView === currentView) render(snapshot);
    showError(null);
  } catch (error) { showError(error); }
  finally {
    loading = false;
    if (reloadRequested) { reloadRequested = false; scheduleRefresh(); }
  }
}

function scheduleRefresh() {
  clearTimeout(reloadTimer);
  reloadTimer = setTimeout(refreshJobs, 150);
}

async function refreshSummary() {
  try {
    const [statistics, system] = await Promise.all([getJson("/api/statistics"), getJson("/api/system")]);
    const nextDestinations = system.servers.map((server) => ({ id: server.id, name: server.name }));
    if (JSON.stringify(nextDestinations) !== JSON.stringify(configuredDestinations)) {
      configuredDestinations = nextDestinations;
      scheduleRefresh();
    }
    const values = [["Imported", statistics.imported], ["Native", statistics.nativeImported], ["Transcoded", statistics.transcodedImported], ["Proxies", statistics.proxyCompletions]];
    byId("statistics").replaceChildren(element("span", "LAST 24 HOURS", "eyebrow"), ...values.map(([label, count]) => {
      const item = element("span", null, "summary-value"); item.append(element("strong", count), element("span", label)); return item;
    }));
    setProcessingNotice(system);
  } catch { /* The primary table request displays API failures without clearing old counters. */ }
}

for (const button of document.querySelectorAll("[data-view]")) button.addEventListener("click", () => {
  currentView = button.dataset.view;
  for (const tab of document.querySelectorAll("[data-view]")) tab.setAttribute("aria-selected", String(tab === button));
  byId("activity-panel").setAttribute("aria-labelledby", button.id);
  byId("view-description").textContent = currentView === "active" ? "Last hour, plus unresolved files still in Watch." : "Older files remain visible while their source exists in Finished or Failed.";
  refreshJobs();
});
byId("refresh-button").addEventListener("click", () => { refreshJobs(); refreshSummary(); });
connectEvents(scheduleRefresh);
refreshJobs();
refreshSummary();
setInterval(refreshJobs, 30000);
setInterval(refreshSummary, 10000);
