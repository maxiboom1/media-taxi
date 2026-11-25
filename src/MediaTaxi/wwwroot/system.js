import { byId, getJson, element, badge, showError, formatDate, formatBytes, formatUptime, connectEvents, setProcessingNotice, metricLabels } from "/common.js";

let loading = false;
let refreshTimer;

function cell(text, className) { return element("td", text, className); }

function renderStatistics(statistics) {
  byId("statistics").replaceChildren(...metricLabels.map(([key, label, description]) => {
    const card = element("article", null, "metric-card");
    card.title = description;
    card.append(element("span", label), element("strong", statistics[key] ?? 0), element("small", "Last 24 hours"));
    return card;
  }));
}

function renderList(id, messages, emptyText) {
  byId(id).replaceChildren(...(messages.length ? messages : [emptyText]).map((message) => element("li", message)));
  byId(id).classList.toggle("muted", messages.length === 0);
}

function renderSystem(system) {
  byId("system-meta").textContent = `Version ${system.version} · Uptime ${formatUptime(system.uptimeSeconds)}`;
  const ready = byId("ready-badge");
  ready.textContent = system.processingReady ? "Processing available" : "Processing paused";
  ready.className = `badge ${system.processingReady ? "success" : "failure"}`;
  setProcessingNotice(system);
  byId("updated").textContent = `Updated ${formatDate(new Date().toISOString())}`;

  byId("server-body").replaceChildren(...system.servers.map((server) => {
    const row = element("tr");
    const connection = cell(); connection.append(badge(server.connected ? "Connected" : "Disconnected", server.connected ? "success" : "warning"));
    const inventory = cell(); inventory.append(badge(server.ready ? "Fresh / ready" : "Unknown / stale", server.ready ? "success" : "warning"));
    row.append(cell(server.name), connection, inventory, cell(formatDate(server.lastInventoryUtc)), cell(server.error || "—", server.error ? "failure-text" : "muted"));
    return row;
  }));
  if (!system.servers.length) { const row = element("tr"); const text = cell("No Tria destinations configured.", "muted"); text.colSpan = 5; row.append(text); byId("server-body").append(row); }

  byId("folder-body").replaceChildren(...system.folders.map((folder) => {
    const row = element("tr");
    const read = cell(); read.append(badge(folder.readable ? "OK" : "Unavailable", folder.readable ? "success" : "failure"));
    const write = cell(); write.append(badge(folder.writable ? "OK" : "Unavailable", folder.writable ? "success" : "failure"));
    row.append(cell(folder.id), cell(folder.path, "path-cell"), read, write, cell(formatBytes(folder.freeBytes)), cell(folder.error || "—", folder.error ? "failure-text" : "muted"));
    return row;
  }));

  byId("media-ready").textContent = system.media.ready ? "Ready" : "Unavailable";
  byId("media-ready").className = `badge ${system.media.ready ? "success" : "failure"}`;
  const details = [];
  for (const [label, value] of [["FFmpeg", system.media.ffmpegVersion], ["ffprobe", system.media.ffprobeVersion], ["Capabilities", system.media.capabilities.join(", ") || "None confirmed"], ["Error", system.media.error || "None"]]) {
    details.push(element("dt", label), element("dd", value || "Not detected"));
  }
  byId("media-details").replaceChildren(...details);
  byId("queue-count").textContent = `${system.queueCount} queued`;
  byId("workers").replaceChildren(...Object.entries(system.workers).map(([name, count]) => {
    const item = element("div", null, "worker-count"); item.append(element("span", name), element("strong", count)); return item;
  }));
  const stageLabels = { Waiting: "Waiting for copy", Queued: "Pending", Probing: "Probing", Wrapping: "Native rewrap", Transcoding: "Transcoding", Delivering: "Delivering", Archiving: "Archiving", Proxy: "Generating proxy", MoveFailed: "Move failed", CleanupPending: "Cleanup pending" };
  const stages = Object.entries(system.stages || {});
  byId("stages").replaceChildren(...(stages.length ? stages.map(([name, count]) => {
    const item = element("div", null, "worker-count"); item.append(element("span", stageLabels[name] || name), element("strong", count)); return item;
  }) : [element("span", "No active jobs.", "muted")]));
  renderList("cleanup-list", system.cleanupPending, "No cleanup pending.");
  renderList("error-list", system.errors, "No current system errors.");
}

async function refresh() {
  if (loading) return;
  loading = true;
  try {
    const [system, statistics] = await Promise.all([getJson("/api/system"), getJson("/api/statistics")]);
    renderSystem(system);
    renderStatistics(statistics);
    showError(null);
  } catch (error) { showError(error); }
  finally { loading = false; }
}

connectEvents(() => { clearTimeout(refreshTimer); refreshTimer = setTimeout(refresh, 300); });
refresh();
setInterval(refresh, 10000);
