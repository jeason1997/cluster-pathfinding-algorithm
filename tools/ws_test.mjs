const ws = new WebSocket("ws://127.0.0.1:8799/ws");
const queue = [];
const waiters = [];
ws.addEventListener("message", e => {
  const msg = JSON.parse(e.data);
  if (waiters.length) waiters.shift()(msg);
  else queue.push(msg);
});
function next(timeoutMs = 5000) {
  if (queue.length) return Promise.resolve(queue.shift());
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("timeout waiting for message")), timeoutMs);
    waiters.push(msg => { clearTimeout(timer); resolve(msg); });
  });
}
async function nextType(type, timeoutMs = 5000) {
  const until = Date.now() + timeoutMs;
  for (;;) {
    const msg = await next(Math.max(100, until - Date.now()));
    if (msg.type === type) return msg;
  }
}
function send(obj) { ws.send(JSON.stringify(obj)); }
function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }

await new Promise((resolve, reject) => {
  ws.addEventListener("open", resolve);
  ws.addEventListener("error", reject);
});
const world = await nextType("world");
const init = await nextType("init");
console.log("world", world.terrain.width, world.terrain.height, "obstacles", world.obstacles.length);
console.log("init count", init.count, "hasGoal", init.hasGoal, "agents", init.agents.length);

send({ type: "speed", value: 4 });
send({ type: "reset", count: 120 });
const reset = await nextType("reset");
console.log("reset", reset.count, "idle agents", reset.agents.length, "hasGoal", reset.hasGoal);

send({ type: "goal", x: 49, z: 48 });
const ack = await nextType("goal");
console.log("goal ack", ack.ok, ack.goal, "slots", ack.slots.length, "radius", ack.goalRadius);

let minSep = 99, maxArrived = 0, finalized = false, states = 0;
const start = Date.now();
while (Date.now() - start < 17000) {
  const msg = await next(4000);
  if (msg.type !== "state") continue;
  states++;
  minSep = Math.min(minSep, msg.minSep);
  maxArrived = Math.max(maxArrived, msg.arrived);
  finalized = finalized || msg.finalized;
  if (states % 30 === 0) {
    console.log("t", msg.t.toFixed(1), "arrived", msg.arrived, "/", msg.count,
      "minSep", msg.minSep.toFixed(3), "yields", msg.yields, "pending", msg.pending);
  }
  if (msg.finalized) break;
}
console.log("RESULT", { states, minSep, maxArrived, finalized });

send({ type: "reset", count: 200 });
const reset200 = await nextType("reset", 8000);
console.log("reset200", reset200.count, "agents", reset200.agents.length);
send({ type: "goal", x: 30, z: 50 });
const ack200 = await nextType("goal", 8000);
console.log("goal200", ack200.ok, ack200.goal, "slots", ack200.slots ? ack200.slots.length : 0);
await sleep(1500);
let state200 = null;
for (;;) {
  const msg = await next(3000);
  if (msg.type === "state") state200 = msg;
  if (state200) break;
}
console.log("state200", state200.count, "hasGoal", state200.hasGoal, "goal", state200.goal, "minSep", state200.minSep);
ws.close();
