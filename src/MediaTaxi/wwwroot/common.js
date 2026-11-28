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

export function formatDate(value, timeZone) {
  if (!value) return "Never";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "Unknown";
  try {
    return new Intl.DateTimeFormat(undefined, { timeZone, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" }).format(date);
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
  source.addEventListener("open", () => { window.dispatchEvent(new CustomEvent("taxi-connection", { detail: true })); onChange(); });
  source.addEventListener("change", onChange);
  source.addEventListener("error", () => window.dispatchEvent(new CustomEvent("taxi-connection", { detail: false })));
  window.addEventListener("pagehide", () => source.close(), { once: true });
  return source;
}
