import { byId, getJson, element } from "/common.js";
import { attachTooltip } from "/tooltip.js";

const errors = new Map();
let system, notification = "Connecting to Media Taxi…";
export function showRequestError(key, error) { if (error) errors.set(key, error.message || String(error)); else errors.delete(key); renderMessage(); }
export function connectionChanged(connected) { showRequestError("events", connected ? null : new Error("Dashboard updates disconnected; showing the last received snapshot.")); }
export function setShellSystem(value) { system = value; renderMessage(); }
function renderMessage() {
  const messages = [...errors.values()];
  if (system) {
    if (!system.processingReady) messages.push(`Processing paused: ${system.pauseReason || "required checks have not passed"}`);
    messages.push(...system.errors);
    for (const server of system.servers.filter(item => !item.ready)) messages.push(`${server.name}: ${server.error || "OGP inventory unavailable"}`);
    if (system.cleanupPending?.length) messages.push(`Cleanup pending: ${system.cleanupPending.join("; ")}`);
    if (system.logging?.error) messages.push(`Logging: ${system.logging.error}`);
    if (system.logging?.droppedMessages) messages.push(`Dropped log messages: ${system.logging.droppedMessages}`);
    messages.push(...(system.sourceBlockers || []));
  }
  const unique = [...new Set(messages)]; notification = unique.join("\n") || "Ready";
  const tone = errors.size || system && !system.processingReady ? "failure-text" : unique.length ? "warning-text" : "muted";
  const node = byId("footer-message"); node.textContent = unique.length > 1 ? `${unique[0]} (+${unique.length - 1})` : notification; node.className = tone;
}
export function startShell(onSystem = setShellSystem) {
  attachTooltip(byId("footer-message"), () => ({ text: notification }));
  async function refresh() {
    try {
      const [value, statistics] = await Promise.all([getJson("/api/system"), getJson("/api/statistics")]);
      system = value; onSystem(value); showRequestError("system", null);
      byId("footer-statistics").replaceChildren(element("span", "24h", "muted"), ...[["Imported", statistics.imported], ["Native", statistics.nativeImported], ["Transcoded", statistics.transcodedImported], ["Proxies", statistics.proxyCompletions]].map(([name, count]) => element("span", `${name} ${count ?? 0}`)));
      window.dispatchEvent(new CustomEvent("taxi-system", { detail: { system: value, statistics } }));
    } catch (error) { showRequestError("system", error); }
  }
  refresh(); setInterval(refresh, 10000);
}

window.addEventListener("taxi-connection", event => connectionChanged(event.detail));
