const ws = new WebSocket("ws://127.0.0.1:8799/ws");
const q = [], wait = [];
ws.addEventListener("message", e => { const m = JSON.parse(e.data); wait.length ? wait.shift()(m) : q.push(m); });
function next(t = 20000) { if (q.length) return Promise.resolve(q.shift()); return new Promise((res, rej) => { const z = setTimeout(() => rej(new Error("timeout")), t); wait.push(m => { clearTimeout(z); res(m); }); }); }
async function type(t, timeout = 30000) { const end = Date.now() + timeout; for (;;) { const m = await next(Math.max(100, end - Date.now())); if (m.type === t) return m; } }
const send = o => ws.send(JSON.stringify(o));
await new Promise((r, j) => { ws.addEventListener("open", r); ws.addEventListener("error", j); });
await type("world"); await type("init");
send({ type: "map", id: "neicheng_walk01", count: 1000 });
const w = await type("world"); const r = await type("reset");
const s = await type("state");
console.log({ map: w.mapId, unitRadius: w.unitRadius, resetCount: r.count, stateCount: s.count, agents: s.agents.length, finalized: s.finalized });
ws.close();
