// Execute the shipped dashboard against a small DOM adapter; no browser/runtime dependency is shipped.
import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../src/MediaTaxi/wwwroot/", import.meta.url));
class Node {
  constructor(tag) { this.tag = tag; this.children = []; this.dataset = {}; this.listeners = {}; this.classList = { toggle() {} }; this.text = ""; }
  get childNodes() { return this.children; }
  get lastChild() { return this.children.at(-1); }
  get firstElementChild() { return this.children[0]; }
  get lastElementChild() { return this.children.at(-1); }
  set textContent(text) { this.text = String(text); this.replaceChildren(); }
  get textContent() { return this.text + this.children.map(child => child.textContent).join(""); }
  get innerHTML() { return JSON.stringify([this.tag, this.textContent, this.value, this.children.map(child => child.innerHTML)]); }
  append(...nodes) { for (const node of nodes) { node.remove(); node.parent = this; this.children.push(node); } }
  remove() { if (this.parent) { this.parent.children.splice(this.parent.children.indexOf(this), 1); this.parent = undefined; } }
  replaceChildren(...nodes) { for (const child of [...this.children]) child.remove(); this.append(...nodes); }
  insertBefore(node, reference) { node.remove(); node.parent = this; const index = reference ? this.children.indexOf(reference) : this.children.length; this.children.splice(index, 0, node); }
  setAttribute(key, value) { this[key] = value; }
  addEventListener(key, listener) { this.listeners[key] = listener; }
  showModal() { this.open = true; }
  close() { this.open = false; this.listeners.close?.(); }
}
const nodes = new Map([...fs.readFileSync(root + "index.html", "utf8").matchAll(/id="([^"]+)"/g)].map(match => [match[1], new Node("div")]));
const byId = id => { assert(nodes.has(id), `Missing HTML element ${id}`); return nodes.get(id); };
const element = (tag, text, className) => { const node = new Node(tag); if (text != null) node.textContent = text; node.className = className; return node; };
byId("empty-state").append(element("strong"), element("span"));
const tabs = [byId("active-tab"), byId("older-tab")]; tabs[0].dataset.view = "active"; tabs[1].dataset.view = "older";
const jobs = Array.from({ length: 500 }, (_, index) => ({ id: String(index), originalName: `clip-${index}.mxf`, clipName: `clip-${index}`, arrivalUtc: new Date(1800000000000 - index * 1000).toISOString(),
  stage: "Wrapping", location: "Watch", mode: "Native", progress: 1, proxyStatus: "Pending", mediaSummary: "AVC-Intra", destinations: [] }));
let snapshot = { revision: 1, timeZone: "Asia/Jerusalem", activeCount: 500, olderCount: 0, jobs };
const timers = new Map(); let timerId = 0; let requests = 0;
const context = vm.createContext({ console, document: { querySelectorAll: () => tabs }, byId, element, badge: element.bind(null, "span"), showError: error => assert(!error),
  formatDate: value => value, setProcessingNotice() {}, connectEvents() {}, Date, setInterval() {}, clearTimeout: id => timers.delete(id),
  setTimeout: callback => { timers.set(++timerId, callback); return timerId; },
  getJson: async url => {
    if (url.startsWith("/api/jobs/")) return { job: jobs[0], diagnostic: { detail: "Full error" } };
    if (url.startsWith("/api/jobs")) { requests++; return snapshot; }
    if (url === "/api/system") return { servers: [] };
    return {};
  }
});
const source = fs.readFileSync(root + "app.js", "utf8").replace(/^import .*\r?\n/, "");
vm.runInContext(source, context);
await new Promise(setImmediate);
assert.equal(byId("activity-body").children.length, 50);
assert.equal(byId("page-label").textContent, "Page 1 of 10");
const firstRow = byId("activity-body").children[0], detailsButton = firstRow.children[1].children[0];
await detailsButton.listeners.click(); await new Promise(setImmediate);
assert.equal(byId("job-dialog").open, true); assert(byId("detail-content").textContent.includes("Full error"));
snapshot = { ...snapshot, jobs: jobs.map((job, index) => index === 0 ? { ...job, progress: 75, error: "x".repeat(1024) } : job) };
await vm.runInContext("refreshJobs()", context);
assert.equal(byId("activity-body").children[0], firstRow);
assert.equal(firstRow.children[1].children[0], detailsButton);
assert.equal(byId("job-dialog").open, true);
byId("next-page").listeners.click(); assert.equal(byId("page-label").textContent, "Page 2 of 10");
byId("search").listeners.input({ target: { value: "clip-499.mxf" } });
assert.equal(byId("activity-body").children.length, 1); assert.equal(byId("page-label").textContent, "Page 1 of 1");
assert(byId("row-count").textContent.includes("1 matching / 500"));
for (let n = 0; n < 100; n++) vm.runInContext("scheduleRefresh()", context);
assert.equal(timers.size, 1, "Continuous SSE updates must share one trailing timer");
const [id, callback] = timers.entries().next().value; timers.delete(id); callback(); await new Promise(setImmediate);
assert(requests >= 3);
const css = fs.readFileSync(root + "styles.css", "utf8"); assert(css.includes("-webkit-line-clamp: 2"));
console.log("Dashboard passed: 500 entries, paging/search, keyed updates/details, bounded errors, trailing refresh.");
