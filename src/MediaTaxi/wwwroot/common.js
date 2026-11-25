export const byId = (id) => document.getElementById(id);

export async function getJson(url) {
  const response = await fetch(url, { cache: "no-store" });
  if (!response.ok) throw new Error(`${url}: HTTP ${response.status}`);
  return response.json();
}

export function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined && text !== null) node.textContent = String(text);
  if (className) node.className = className;
  return node;
}

export function badge(text, tone = "neutral") {
  return element("span", text, `badge ${tone}`);
}

export function showError(error) {
  const node = byId("page-error");
  node.textContent = error ? `Unable to refresh: ${error.message || error}. Last received data remains displayed.` : "";
  node.classList.toggle("hidden", !error);
}

export function setConnection(connected, label) {
  const node = byId("connection");
  node.classList.toggle("connected", connected);
  node.replaceChildren(element("span", "", "dot"), element("span", label));
}

export function formatDate(value, timeZone) {
  if (!value) return "Never";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "Unknown";
  try {
    return new Intl.DateTimeFormat(undefined, { timeZone, month: "short", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" }).format(date);
  } catch {
    return date.toLocaleString();
  }
}

export function formatBytes(value) {
  if (value === null || value === undefined) return "Unavailable";
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  let size = Number(value), index = 0;
  while (size >= 1024 && index < units.length - 1) { size /= 1024; index++; }
  return `${size.toFixed(index < 2 ? 0 : 1)} ${units[index]}`;
}

export function formatUptime(value) {
  const total = Math.max(0, Math.floor(Number(value) || 0));
  const days = Math.floor(total / 86400), hours = Math.floor(total / 3600) % 24, minutes = Math.floor(total / 60) % 60;
  return `${days ? `${days}d ` : ""}${hours}h ${minutes}m`;
}

export function connectEvents(onChange) {
  const source = new EventSource("/api/events");
  source.addEventListener("open", () => { setConnection(true, "Live"); onChange(); });
  source.addEventListener("change", onChange);
  source.addEventListener("error", () => setConnection(false, "Reconnecting"));
  window.addEventListener("pagehide", () => source.close(), { once: true });
  return source;
}

export function setProcessingNotice(system) {
  const notice = byId("processing-notice");
  notice.textContent = system.processingReady ? "" : `Processing paused: ${system.pauseReason || "a required system check has not passed"}`;
  notice.classList.toggle("hidden", system.processingReady);
}

export const metricLabels = [
  ["imported", "Imported", "First successful Media Taxi delivery"],
  ["nativeImported", "Native imports", "Video copied without re-encoding"],
  ["transcodedImported", "Transcoded imports", "Input normalized before wrapping"],
  ["processed", "Processed", "Verified local clips created"],
  ["deliveryFailures", "Delivery failures", "All destination attempts failed"],
  ["processingFailures", "Processing failures", "Terminal ingest errors"],
  ["proxyCompletions", "Proxies ready", "MP4 generation completed"]
];
