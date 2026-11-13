import fs from "node:fs";

const sleep = ms => new Promise(r => setTimeout(r, ms));
const targets = await fetch("http://127.0.0.1:9223/json/list").then(r => r.json());
const page = targets.find(t => t.type === "page");
if (!page) throw new Error("no page target");
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  ws.addEventListener("open", resolve);
  ws.addEventListener("error", reject);
});

let nextId = 0;
const pending = new Map();
const exceptions = [];
ws.addEventListener("message", ev => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) {
    const { resolve, reject } = pending.get(msg.id);
    pending.delete(msg.id);
    if (msg.error) reject(new Error(JSON.stringify(msg.error)));
    else resolve(msg.result);
  }
  if (msg.method === "Runtime.exceptionThrown") {
    exceptions.push(msg.params.exceptionDetails.text);
  }
});

function cmd(method, params = {}) {
  const id = ++nextId;
  ws.send(JSON.stringify({ id, method, params }));
  return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
}

async function evaluate(expression) {
  const r = await cmd("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.text || "evaluate failed");
  return r.result.value;
}

async function screenshot(path) {
  const r = await cmd("Page.captureScreenshot", { format: "png" });
  fs.writeFileSync(path, Buffer.from(r.data, "base64"));
  console.log("screenshot", path, fs.statSync(path).size);
}

await cmd("Page.enable");
await cmd("Runtime.enable");
await cmd("Emulation.setDeviceMetricsOverride", {
  width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false
});
await cmd("Page.navigate", { url: "http://127.0.0.1:8799/" });
await sleep(3000);

console.log("title", await evaluate("document.title"));
console.log("connected", await evaluate("document.getElementById('connText').textContent"));
console.log("initial", await evaluate(`({
  units: document.getElementById('ovUnits').textContent,
  state: document.getElementById('ovState').textContent,
  goal: document.getElementById('goalText').textContent
})`));
console.log("demo hook", await evaluate("!!window.__demo"));

// 4x so the screenshot shows the crowd well into/at the destination.
await evaluate(`(() => {
  const s = document.getElementById('speedSelect');
  s.value = '4';
  s.dispatchEvent(new Event('change', { bubbles: true }));
  return true;
})()`);
await sleep(300);

const clickPoint = await evaluate("window.__demo.screenForWorld(49,48)");
console.log("click at", clickPoint);
await cmd("Input.dispatchMouseEvent", { type: "mouseMoved", x: clickPoint.x, y: clickPoint.y });
await cmd("Input.dispatchMouseEvent", { type: "mousePressed", x: clickPoint.x, y: clickPoint.y, button: "left", buttons: 1, clickCount: 1 });
await cmd("Input.dispatchMouseEvent", { type: "mouseReleased", x: clickPoint.x, y: clickPoint.y, button: "left", buttons: 0, clickCount: 1 });
await sleep(1200);
console.log("goal after click", await evaluate("document.getElementById('goalText').textContent"));
console.log("toast", await evaluate("document.getElementById('toast').textContent"));

await sleep(12000);
console.log("after 12s", await evaluate(`({
  time: document.getElementById('tNow').textContent,
  units: document.getElementById('ovUnits').textContent,
  arrived: document.getElementById('ovArrived').textContent,
  minSep: document.getElementById('ovMin').textContent,
  state: document.getElementById('ovState').textContent,
  goal: document.getElementById('goalText').textContent,
  step: document.getElementById('sStep').textContent
})`));
await screenshot("out/interactive_live.png");

// Count parameter + reset, then click a second destination.
await evaluate(`(() => {
  document.getElementById('countNumber').value = 200;
  document.getElementById('applyCount').click();
  return true;
})()`);
await sleep(2200);
console.log("after count reset", await evaluate(`({
  units: document.getElementById('ovUnits').textContent,
  total: document.getElementById('sTotal').textContent,
  arrived: document.getElementById('ovArrived').textContent,
  goal: document.getElementById('goalText').textContent,
  state: document.getElementById('ovState').textContent
})`));

const click2 = await evaluate("window.__demo.screenForWorld(30,50)");
await cmd("Input.dispatchMouseEvent", { type: "mousePressed", x: click2.x, y: click2.y, button: "left", buttons: 1, clickCount: 1 });
await cmd("Input.dispatchMouseEvent", { type: "mouseReleased", x: click2.x, y: click2.y, button: "left", buttons: 0, clickCount: 1 });
await sleep(1500);
console.log("goal after count200 click", await evaluate("document.getElementById('goalText').textContent"));
await sleep(5000);
console.log("count200 after 6.5s", await evaluate(`({
  time: document.getElementById('tNow').textContent,
  units: document.getElementById('ovUnits').textContent,
  arrived: document.getElementById('ovArrived').textContent,
  minSep: document.getElementById('ovMin').textContent,
  state: document.getElementById('ovState').textContent,
  goal: document.getElementById('goalText').textContent
})`));
await screenshot("out/interactive_live_200.png");
console.log("exceptions", exceptions);
ws.close();
