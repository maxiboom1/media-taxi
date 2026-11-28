import { element } from "/common.js";

const state = (label, tone = "neutral", progress = 0, detail = label, failure = false, active = false) => ({ label, tone, progress, detail, failure, active });
const failed = (detail, label = "Failed") => state(label, "red", 100, detail || "Operation failed. Hover for details.", true);
const percent = value => Math.max(0, Math.min(99, Number(value) || 0));
export function processingState(job) {
  if (job.hiresPhase === "Failed" || job.stage === "MoveFailed" || job.stage === "Failed" && job.proxyStatus !== "Failed") return failed(job.error);
  if (job.waitCode === "SourceBlocked") return failed(job.waitReason, "Blocked");
  if (job.stage === "Probing" && !job.processingComplete) return state("Probing", "neutral", 0, "Inspecting source video and audio");
  const complete = job.processingComplete || job.hiresPhase === "Complete";
  if (complete || ["Wrapping", "Transcoding"].includes(job.hiresPhase)) {
    const native = job.mode === "Native";
    return state(native ? "Native" : complete ? "Transcoded" : "Transcoding", native ? "green" : "yellow", complete ? 100 : percent(job.progress), complete ? `${native ? "Native rewrap" : "Transcoding"} completed` : `${native ? "Native rewrap" : "Transcoding"}: ${Math.round(percent(job.progress))}%`, false, !complete);
  }
  if (job.stage === "Existing") return job.location === "Failed" ? failed("Source found in Failed; historical reason unavailable.") : state("Existing", "neutral", 0, "Existing source; no import was started");
  return state(job.stage === "Waiting" ? "Waiting" : "Pending", "neutral", 0, job.waitReason || job.cacheWaitReason || "Waiting for a processing worker");
}
export function proxyState(job) {
  if (job.proxyStatus === "Failed") return failed(job.error || "Proxy generation failed");
  if (job.proxyStatus === "Ready") return state("Ready", "green", 100, "Proxy completed");
  if (job.proxyStatus === "Working") return state("Working", "green", percent(job.proxyProgress), `Proxy: ${Math.round(percent(job.proxyProgress))}%`, false, true);
  return state(job.proxyStatus || "Pending", "neutral", 0, job.proxyDeferralReason || "Waiting for proxy generation");
}
export function destinationState(job, id) {
  const dest = job.destinations.find(item => item.id === id);
  if (!dest) return state("Unknown", "yellow", 100, "Destination information unavailable");
  if (dest.copyStatus === "Failed") return failed(dest.error);
  if (dest.copyStatus === "InProgress") return state("Copying", "yellow", percent(dest.copyProgress), `Copying: ${Math.round(percent(dest.copyProgress))}%`, false, true);
  if (dest.copyStatus === "Succeeded" && !dest.inventoryConfirmed) return state("Copying", "yellow", 100, "Copy published; waiting for fresh OGP presence");
  if (dest.copyStatus === "Succeeded" || job.stage === "Existing") {
    if (dest.availability === "OK") return state("OK", "green", 100, "Fresh OGP inventory confirms clip presence");
    if (dest.availability === "Missing") return failed("Clip is absent from fresh OGP inventory", "Missing");
    if (dest.availability === "Removed") return state("Removed", "neutral", 0, "Previously online; now absent from fresh OGP inventory");
    return state("Unknown", "yellow", 100, "OGP inventory is unavailable or stale");
  }
  return state("Pending", "neutral", 0, "No delivery attempt has started");
}
export function createIndicator() {
  const box = element("div", null, "status-box"); box.tabIndex = 0;
  const label = element("span", "Pending", "status-label");
  const fill = element("span", null, "status-fill"); fill.setAttribute("aria-hidden", "true"); fill.append(element("span", "Pending", "status-label"));
  box.append(label, fill); return box;
}
export function updateIndicator(box, value) {
  const signature = JSON.stringify([value.label, value.tone, value.progress, value.active]);
  if (box.dataset.state === signature) return;
  box.dataset.state = signature; box.className = `status-box ${value.tone}`;
  box.style.setProperty("--progress", `${value.progress}%`);
  box.children[0].textContent = value.label; box.children[1].children[0].textContent = value.label;
  box.setAttribute("role", value.active ? "progressbar" : "img"); box.setAttribute("aria-label", value.label);
  if (value.active) { box.setAttribute("aria-valuemin", "0"); box.setAttribute("aria-valuemax", "100"); box.setAttribute("aria-valuenow", String(value.progress)); }
  else for (const name of ["aria-valuemin", "aria-valuemax", "aria-valuenow"]) box.removeAttribute(name);
}
