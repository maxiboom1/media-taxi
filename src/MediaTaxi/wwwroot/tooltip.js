import { element, getJson } from "/common.js";

const tooltip = element("div", null, "status-tooltip"); tooltip.id = "status-tooltip"; tooltip.setAttribute("role", "tooltip"); tooltip.hidden = true; document.body.append(tooltip);
const cache = new Map();
let anchor, hideTimer, token = 0;
function hide() { token++; if (anchor) anchor.removeAttribute("aria-describedby"); anchor = undefined; tooltip.hidden = true; }
function closeSoon() { clearTimeout(hideTimer); hideTimer = setTimeout(hide, 180); }
function position() {
  if (!anchor?.isConnected) { hide(); return; }
  const rect = anchor.getBoundingClientRect(), gap = 7;
  const width = tooltip.getBoundingClientRect().width;
  tooltip.style.left = `${Math.max(8, Math.min(rect.left, innerWidth - width - 8))}px`;
  const height = tooltip.getBoundingClientRect().height;
  tooltip.style.top = `${Math.max(8, Math.min(rect.bottom + gap, innerHeight - height - 8))}px`;
}
async function show(node, content) {
  clearTimeout(hideTimer); hide(); const ownToken = ++token; anchor = node;
  node.setAttribute("aria-describedby", tooltip.id); tooltip.textContent = content.text || "No additional details"; tooltip.hidden = false; position();
  if (!content.failure || !content.jobId) return;
  const key = JSON.stringify([content.jobId, content.scope, content.text]);
  try {
    let detail = cache.get(key);
    if (!detail) {
      const response = await getJson(`/api/jobs/${encodeURIComponent(content.jobId)}`);
      detail = content.scope === "processing" || content.scope === "proxy" ? response.diagnostic?.detail : response.destinationDiagnostics?.[content.scope];
      detail = detail || content.text;
      if (cache.size >= 100) cache.delete(cache.keys().next().value);
      cache.set(key, detail);
    }
    if (ownToken === token) { tooltip.textContent = detail; position(); }
  } catch { /* Keep the available error; retirement is not a system failure. */ }
}
export function attachTooltip(node, getContent) {
  node.addEventListener("pointerenter", () => show(node, getContent())); node.addEventListener("focus", () => show(node, getContent()));
  node.addEventListener("pointerleave", closeSoon); node.addEventListener("blur", closeSoon);
}
tooltip.addEventListener("pointerenter", () => clearTimeout(hideTimer)); tooltip.addEventListener("pointerleave", closeSoon);
document.addEventListener("keydown", event => { if (event.key === "Escape") hide(); });
document.addEventListener("scroll", () => { if (!tooltip.hidden) position(); }, true);
window.addEventListener("resize", () => { if (!tooltip.hidden) position(); });
