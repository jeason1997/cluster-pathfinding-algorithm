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
await sleep(3000);
await ev(`(() => { const s=document.getElementById('mapSelect'); s.value='neicheng_walk01'; s.dispatchEvent(new Event('change',{bubbles:true})); return true; })()`);
await sleep(3000);
console.log("mesh map", await ev(`({map:document.getElementById('mapMeta').textContent, units:document.getElementById('ovUnits').textContent})`));
await shot("out/neicheng_mesh_exact.png");

// Change unit size and movement speed, then apply.
await ev(`(() => { const z=document.getElementById('sizeRange'); z.value='1.6'; z.dispatchEvent(new Event('input',{bubbles:true}));
  const v=document.getElementById('unitSpeedRange'); v.value='4.5'; v.dispatchEvent(new Event('input',{bubbles:true}));
  document.getElementById('applyUnits').click(); return true; })()`);
await sleep(3000);
console.log("units", await ev(`({size:document.getElementById('sizeLabel').textContent,
  speed:document.getElementById('unitSpeedLabel').textContent,
  count:document.getElementById('ovUnits').textContent,
  state:document.getElementById('ovState').textContent})`));
await ev(`document.getElementById('suggestGoalBtn').click()`);
await sleep(5000);
await shot("out/neicheng_units.png");
console.log("after", await ev(`({t:document.getElementById('tNow').textContent,
  arrived:document.getElementById('ovArrived').textContent,
  count:document.getElementById('ovUnits').textContent,
  state:document.getElementById('ovState').textContent,
  min:document.getElementById('ovMin').textContent})`));
console.log("exceptions", exceptions);
ws.close();
