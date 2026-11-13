const ws = new WebSocket("ws://127.0.0.1:8799/ws");
const q = [], wait = [];
ws.addEventListener("message", e => { const m = JSON.parse(e.data); wait.length ? wait.shift()(m) : q.push(m); });
function next(t = 20000) { if (q.length) return Promise.resolve(q.shift()); return new Promise((res, rej) => { const z = setTimeout(() => rej(new Error("timeout")), t); wait.push(m => { clearTimeout(z); res(m); }); }); }
async function type(t, timeout = 30000) { const end = Date.now() + timeout; for (;;) { const m = await next(Math.max(100, end - Date.now())); if (m.type === t) return m; } }
const send = o => ws.send(JSON.stringify(o));
await new Promise((r, j) => { ws.addEventListener("open", r); ws.addEventListener("error", j); });
await type("world"); await type("init");
send({ type: "map", id: "neicheng_walk01", count: 1000 });
const w = await type("world"); await type("reset");
send({ type: "goal", x: w.suggestedGoal[0], z: w.suggestedGoal[1] });
await type("goal");
const t0 = Date.now(); let n = 0, bytes = 0, maxStep = 0, maxPending = 0, startT = 0, lastT = 0;
while (Date.now() - t0 < 8000) {
  const m = await next(5000); if (m.type !== "state") continue;
  n++; lastT = m.t; if (!startT) startT = m.t;
  bytes += JSON.stringify(m).length;
  maxStep = Math.max(maxStep, m.stepMs || 0); maxPending = Math.max(maxPending, m.pending || 0);
}
console.log({ states: n, seconds: (lastT - startT).toFixed(2), avgStateBytes: Math.round(bytes / n),
  maxStepMs: maxStep.toFixed(2), maxPending, arrived: (await Promise.resolve(0)) });
ws.close();
