const ws = new WebSocket("ws://127.0.0.1:8799/ws");
const q = [], wait = [];
ws.addEventListener("message", e => { const m = JSON.parse(e.data); wait.length ? wait.shift()(m) : q.push(m); });
function next(t = 10000) { if (q.length) return Promise.resolve(q.shift()); return new Promise((res, rej) => { const z = setTimeout(() => rej(new Error("timeout")), t); wait.push(m => { clearTimeout(z); res(m); }); }); }
async function type(t, timeout = 15000) { const end = Date.now() + timeout; for (;;) { const m = await next(Math.max(100, end - Date.now())); if (m.type === t) return m; } }
const send = o => ws.send(JSON.stringify(o));
await new Promise((r, j) => { ws.addEventListener("open", r); ws.addEventListener("error", j); });
await type("world"); await type("init");
send({ type: "map", id: "neicheng_walk01", count: 300 });
await type("world"); await type("reset");
send({ type: "units", count: 300, size: 0.5, speed: 3.1 });
await type("world"); await type("reset");
send({ type: "goal", x: 94.97, z: 38.42 });
const a = await type("goal");
console.log(JSON.stringify({ ok: a.ok, error: a.error, goal: a.goal, radius: a.goalRadius }));
ws.close();
