import fs from "node:fs";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const targets = await fetch("http://127.0.0.1:9223/json/list").then(r => r.json());
const page = targets.find(t => t.type === "page");
if (!page) throw new Error("no page");
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.addEventListener("open", res); ws.addEventListener("error", rej); });
let id = 0; const pending = new Map(); const exceptions = [];
ws.addEventListener("message", ev => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.reject(new Error(JSON.stringify(m.error))) : p.resolve(m.result); }
  if (m.method === "Runtime.exceptionThrown") exceptions.push(m.params.exceptionDetails.text);
});
function cmd(method, params = {}) { const n = ++id; ws.send(JSON.stringify({ id: n, method, params })); return new Promise((resolve, reject) => pending.set(n, { resolve, reject })); }
async function ev(expr) { const r = await cmd("Runtime.evaluate", { expression: expr, returnByValue: true, awaitPromise: true }); if (r.exceptionDetails) throw new Error(r.exceptionDetails.text); return r.result.value; }
async function shot(path) {
  const r = await cmd("Page.captureScreenshot", { format: "png" });
  fs.writeFileSync(path, Buffer.from(r.data, "base64"));
  console.log("screenshot", path, fs.statSync(path).size);
}
await cmd("Page.enable"); await cmd("Runtime.enable");
await cmd("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
await cmd("Page.navigate", { url: "http://127.0.0.1:8799/" });
await sleep(3500);
console.log("initial", await ev(`({ map: document.getElementById('mapMeta').textContent,
  options: [...document.getElementById('mapSelect').options].map(o => o.value),
  title: document.title })`));
await ev(`(() => { const s=document.getElementById('mapSelect'); s.value='neicheng_walk01'; s.dispatchEvent(new Event('change',{bubbles:true})); return true; })()`);
await sleep(3500);
console.log("imported", await ev(`({ map: document.getElementById('mapMeta').textContent,
  selected: document.getElementById('mapSelect').value,
  units: document.getElementById('ovUnits').textContent,
  state: document.getElementById('ovState').textContent })`));
await shot("out/neicheng_3d.png");

await ev(`document.getElementById('suggestGoalBtn').click()`);
await sleep(2500);
console.log("goal", await ev(`({ text: document.getElementById('goalText').textContent,
  toast: document.getElementById('toast').textContent,
  state: document.getElementById('ovState').textContent })`));

await ev(`document.querySelector('#viewToggle button[data-view="2d"]').click()`);
await sleep(700);
await shot("out/neicheng_2d.png");
console.log("2d", await ev(`({ mode: window.__demo.getViewMode(), meta: document.getElementById('mapMeta').textContent })`));
console.log("exceptions", exceptions);
ws.close();
