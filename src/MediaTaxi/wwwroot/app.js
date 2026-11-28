import { byId, getJson, element, formatDate, connectEvents } from "/common.js";
import { startShell, showRequestError, setShellSystem } from "/shell.js";
import { createIndicator, updateIndicator, processingState, proxyState, destinationState } from "/indicators.js";
import { attachTooltip } from "/tooltip.js";

const rows = new Map();
let servers = [], columnKey = "", loading = false, requested = false, timer, lastRefresh = 0;
const scroller = byId("activity-scroll"), body = byId("activity-body");

function render(snapshot) {
  const columns = new Map(servers.map(server => [server.id, server]));
  for (const job of snapshot.jobs) for (const destination of job.destinations) if (!columns.has(destination.id)) columns.set(destination.id, destination);
  const destinations = [...columns.values()];
  const key = JSON.stringify(destinations.map(item => [item.id, item.name]));
  if (key !== columnKey) {
    columnKey = key;
    const heading = element("tr");
    for (const name of ["Date", "Clip Name", "Clip Format", "Processing", "Proxy", ...destinations.map(item => item.name)]) {
      const cell = element("th", name); cell.scope = "col"; cell.title = name; heading.append(cell);
    }
    byId("activity-head").replaceChildren(heading);
    byId("activity-table").style.setProperty("--server-count", destinations.length);
    for (const record of rows.values()) record.columnKey = null;
  }
  const awayFromTop = scroller.scrollTop > 2;
  const anchor = awayFromTop ? [...body.children].find(row => row.getBoundingClientRect().bottom > scroller.getBoundingClientRect().top + 36) : null;
  const anchorTop = anchor?.getBoundingClientRect().top;
  const visible = new Set(snapshot.jobs.map(job => job.id));
  for (const [id, record] of rows) if (!visible.has(id)) { record.row.remove(); rows.delete(id); }
  for (const [index, job] of snapshot.jobs.entries()) {
    let record = rows.get(job.id);
    if (!record) {
      const row = element("tr"); row.dataset.id = job.id;
      const cells = [element("td", null, "date-cell"), element("td", null, "name-cell"), element("td", null, "format-cell")];
      const processing = createIndicator(), proxy = createIndicator();
      for (const indicator of [processing, proxy]) { const cell = element("td", null, "indicator-cell"); cell.append(indicator); cells.push(cell); }
      row.append(...cells);
      record = { row, cells, processing, proxy, destinations: new Map(), job }; rows.set(job.id, record);
      attachTooltip(processing, () => ({ text: processingState(record.job).detail, jobId: record.job.id, scope: "processing", failure: processingState(record.job).failure }));
      attachTooltip(proxy, () => ({ text: proxyState(record.job).detail, jobId: record.job.id, scope: "proxy", failure: proxyState(record.job).failure }));
    }
    record.job = job;
    if (record.columnKey !== key) {
      for (const cell of record.destinations.values()) cell.cell.remove(); record.destinations.clear();
      for (const destination of destinations) {
        const cell = element("td", null, "indicator-cell"), indicator = createIndicator(); cell.append(indicator); record.row.append(cell);
        record.destinations.set(destination.id, { cell, indicator });
        attachTooltip(indicator, () => {
          const state = destinationState(record.job, destination.id);
          return { text: state.detail, jobId: record.job.id, scope: destination.id, failure: state.failure };
        });
      }
      record.columnKey = key;
    }
    const values = [formatDate(job.arrivalUtc, snapshot.timeZone), job.clipName || job.originalName, job.mediaSummary || "—"];
    values.forEach((value, i) => { if (record.cells[i].textContent !== value) record.cells[i].textContent = value; });
    record.cells[0].title = job.arrivalUtc; record.cells[1].title = job.originalName;
    record.cells[2].title = job.mediaSummaryDetail || job.mediaSummary || "Not probed";
    updateIndicator(record.processing, processingState(job)); updateIndicator(record.proxy, proxyState(job));
    for (const destination of destinations) updateIndicator(record.destinations.get(destination.id).indicator, destinationState(job, destination.id));
    if (body.children[index] !== record.row) body.insertBefore(record.row, body.children[index] || null);
  }
  byId("empty-state").hidden = snapshot.jobs.length > 0;
  if (anchor?.isConnected) scroller.scrollTop += anchor.getBoundingClientRect().top - anchorTop;
}

async function refresh() {
  if (loading) { requested = true; return; }
  loading = true; lastRefresh = Date.now();
  try { render(await getJson("/api/jobs?view=all")); showRequestError("jobs", null); }
  catch (error) { showRequestError("jobs", error); }
  finally { loading = false; if (requested) { requested = false; schedule(); } }
}
function schedule() {
  if (timer) return;
  timer = setTimeout(() => { timer = undefined; refresh(); }, Math.max(0, 500 - (Date.now() - lastRefresh)));
}
startShell(system => {
  setShellSystem(system);
  if (JSON.stringify(servers) !== JSON.stringify(system.servers)) { servers = system.servers; schedule(); }
});
connectEvents(schedule); schedule(); setInterval(schedule, 30000);
