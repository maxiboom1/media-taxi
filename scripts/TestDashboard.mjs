import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../src/MediaTaxi/wwwroot/", import.meta.url));
class Node {
  constructor(tag) { this.tag = tag; this.children = []; this.dataset = {}; this.listeners = {}; this.attributes = {}; this.hidden = false; this.text = ""; this.style = { setProperty: (key, value) => this.style[key] = value }; this.classList = { toggle() {} }; }
  get childNodes() { return this.children; }
  get isConnected() { return !!this.parent || this.tag === "body"; }
  get textContent() { return this.text + this.children.map(child => child.textContent).join(""); }
  set textContent(value) { this.replaceChildren(); this.text = String(value); }
  append(...nodes) { for (const node of nodes) { node.remove(); node.parent = this; this.children.push(node); } }
  remove() { if (this.parent) { this.parent.children.splice(this.parent.children.indexOf(this), 1); this.parent = undefined; } }
  replaceChildren(...nodes) { for (const node of [...this.children]) node.remove(); this.append(...nodes); }
  insertBefore(node, reference) { node.remove(); node.parent = this; this.children.splice(reference ? this.children.indexOf(reference) : this.children.length, 0, node); }
  setAttribute(key, value) { this.attributes[key] = value; }
  removeAttribute(key) { delete this.attributes[key]; }
  addEventListener(key, listener) { (this.listeners[key] ||= []).push(listener); }
  getBoundingClientRect() { return { top: 100, bottom: 142, left: 10, width: 128, height: 30 }; }
  async fire(key, event = {}) { for (const listener of this.listeners[key] || []) await listener(event); }
}
const body = new Node("body"), document = new Node("document"), window = new Node("window");
const html = fs.readFileSync(root + "index.html", "utf8");
const nodes = new Map([...html.matchAll(/id="([^"]+)"/g)].map(match => [match[1], new Node("div")]));
for (const node of nodes.values()) body.append(node);
document.body = body; document.createElement = tag => new Node(tag); document.getElementById = id => { assert(nodes.has(id), id); return nodes.get(id); };
window.dispatchEvent = event => window.fire(event.type, event);
const timers = new Map(); let timerId = 0;
const destinations = Array.from({ length: 4 }, (_, index) => ({ id: `server-${index}`, name: `TLV${index + 1}`, copyStatus: "Succeeded", inventoryConfirmed: true, availability: "OK" }));
let snapshot = { revision: 1, timeZone: "Asia/Jerusalem", jobs: Array.from({ length: 500 }, (_, index) => ({ id: String(index), clipName: `CLIP_${index}`, originalName: `CLIP_${index}.mxf`, arrivalUtc: "2026-11-28T10:00:00Z", stage: "Done", mode: "Native", hiresPhase: "Complete", processingComplete: true, mediaSummary: "MXF/ Intra 1920x1080 50fps / 2ch / Dur: 1:31", proxyStatus: "Ready", destinations })) };
const system = { servers: destinations.map(item => ({ ...item, ready: true })), processingReady: true, errors: [] };
let requests = 0;
const context = vm.createContext({ console, document, window, innerWidth: 1920, innerHeight: 1080, Intl, Date,
  CustomEvent: class { constructor(type, { detail }) { this.type = type; this.detail = detail; } },
  setInterval() {}, setTimeout: callback => { timers.set(++timerId, callback); return timerId; }, clearTimeout: id => timers.delete(id),
  EventSource: class { addEventListener() {} close() {} },
  fetch: async url => ({ ok: true, json: async () => {
    if (url.startsWith("/api/jobs/")) return { destinationDiagnostics: { "server-0": "FULL ERROR\n" + "x".repeat(3000) } };
    if (url === "/api/jobs?view=all") { requests++; return snapshot; }
    if (url === "/api/system") return system;
    return { imported: 500, nativeImported: 499, transcodedImported: 1, proxyCompletions: 490 };
  } })
});
const modules = new Map();
function moduleFor(name) {
  if (!modules.has(name)) modules.set(name, new vm.SourceTextModule(fs.readFileSync(root + name, "utf8") + (name === "app.js" ? "\nexport { render, schedule };" : ""), { context, identifier: name }));
  return modules.get(name);
}
const app = moduleFor("app.js"); await app.link(name => moduleFor(name.slice(1))); await app.evaluate(); await new Promise(setImmediate);
async function flushTimers() { for (const [id, callback] of [...timers]) { timers.delete(id); await callback(); } await new Promise(setImmediate); }
await flushTimers();
const table = nodes.get("activity-body");
assert.equal(table.children.length, 500); assert.equal(table.children[0].children.length, 9);
const row = table.children[0], nativeBox = row.children[3].children[0];
assert.equal(nativeBox.attributes["aria-label"], "Native"); assert.equal(nativeBox.style["--progress"], "100%");
const indicators = modules.get("indicators.js").namespace;
assert.equal(indicators.processingState({ stage: "Probing" }).progress, 0);
assert.equal(indicators.processingState({ mode: "Transcoded", processingComplete: true }).label, "Transcoded");
assert.equal(indicators.processingState({ mode: "Transcoded", processingComplete: true }).tone, "yellow");
assert.equal(indicators.processingState({ mode: "Native", processingComplete: true, proxyStatus: "Failed", stage: "Done" }).tone, "green");
for (const [availability, label] of [["Removed", "Removed"], ["Unknown", "Unknown"], ["Missing", "Missing"]])
  assert.equal(indicators.destinationState({ destinations: [{ ...destinations[0], availability }] }, "server-0").label, label);
const copying = indicators.destinationState({ destinations: [{ ...destinations[0], inventoryConfirmed: false }] }, "server-0");
assert.equal(copying.label, "Copying"); assert.equal(copying.tone, "yellow"); assert.equal(copying.progress, 100);
snapshot = { ...snapshot, jobs: snapshot.jobs.map((job, index) => index ? job : { ...job, destinations: job.destinations.map((dest, i) => i ? dest : { ...dest, copyStatus: "Failed", error: "Short error" }) }) };
window.dispatchEvent({ type: "taxi-connection", detail: false }); assert(nodes.get("footer-message").textContent.includes("disconnected"));
for (let i = 0; i < 20; i++) app.namespace.schedule();
assert.equal(timers.size, 1); await flushTimers();
app.namespace.render(snapshot);
assert.equal(table.children.length, 500); assert.equal(table.children[0], row); assert.equal(row.children[3].children[0], nativeBox);
const failing = table.children[0].children[5].children[0]; assert.equal(failing.attributes["aria-label"], "Failed");
assert(!table.textContent.includes("Short error")); await failing.fire("focus"); await new Promise(setImmediate);
assert(body.children.at(-1).textContent.includes("FULL ERROR")); await document.fire("keydown", { key: "Escape" }); assert(body.children.at(-1).hidden);
for (const id of ["active-tab", "older-tab", "search", "refresh-button", "next-page", "job-dialog", "connection"]) assert(!html.includes(`id="${id}"`));
const css = fs.readFileSync(root + "styles.css", "utf8"); assert(css.includes("height: 42px")); assert(css.includes("width: 128px; height: 30px"));
assert(requests >= 1);
console.log("Dashboard passed: 500 rows, four destinations, fixed indicators, independent outcomes, full hover diagnostics and footer connection errors.");
