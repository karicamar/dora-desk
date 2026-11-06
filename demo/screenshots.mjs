// Drives DORA Desk in headless Chrome over the DevTools protocol: signs in, clicks through the main screens,
// fails on any console error or CSP violation, and saves screenshots to docs/.
// Start the app first (dotnet run --project src/DoraDesk.Web), then: node demo/screenshots.mjs [chrome path] [base url]
// Needs Node 22+ (built-in WebSocket).

import { spawn } from "node:child_process";
import { mkdirSync, writeFileSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const chrome = process.argv[2] ?? "C:/Program Files/Google/Chrome/Application/chrome.exe";
const base = process.argv[3] ?? "http://localhost:5090";
const docs = new URL("../docs/", import.meta.url);
mkdirSync(docs, { recursive: true });

const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), "desk-chrome-"));
const proc = spawn(chrome, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, "--window-size=1360,900", "--hide-scrollbars", "about:blank"], { stdio: "ignore" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50 && !targets; i++) {
  try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch { await sleep(200); }
}
const page = targets.find((t) => t.type === "page");
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener("open", r));

let id = 0;
const pending = new Map();
const problems = [];
ws.addEventListener("message", (e) => {
  const msg = JSON.parse(e.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  if (msg.method === "Runtime.consoleAPICalled" && msg.params.type === "error")
    problems.push(msg.params.args.map((a) => a.value ?? a.description).join(" "));
  if (msg.method === "Runtime.exceptionThrown") problems.push(msg.params.exceptionDetails.text);
  if (msg.method === "Log.entryAdded" && msg.params.entry.level === "error") problems.push(msg.params.entry.text);
});
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const n = ++id;
  pending.set(n, (m) => (m.error ? reject(new Error(`${method}: ${m.error.message}`)) : resolve(m.result)));
  ws.send(JSON.stringify({ id: n, method, params }));
});
const evaluate = async (expression) => (await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true })).result.value;
const waitFor = async (expression, what, timeout = 15000) => {
  for (const start = Date.now(); Date.now() - start < timeout; await sleep(150))
    if (await evaluate(expression)) return;
  throw new Error(`Timed out waiting for ${what}`);
};
const go = async (path) => { await send("Page.navigate", { url: base + path }); await sleep(400); await waitFor("document.readyState === 'complete'", path); };
// An interactive page is ready when Blazor has connected its circuit
const interactive = () => waitFor("!!window.Blazor && !!document.querySelector('[data-ready], h1') && performance.getEntriesByType('resource').some(r => r.name.includes('_blazor'))", "the Blazor circuit");
const click = (selector) => evaluate(`(() => { const el = document.querySelector(${JSON.stringify(selector)}); if (!el) return false; el.click(); return true; })()`);
const clickText = (selector, text) => evaluate(`(() => { const el = [...document.querySelectorAll(${JSON.stringify(selector)})].find(e => e.textContent.includes(${JSON.stringify(text)})); if (!el) return false; el.click(); return true; })()`);
const shot = async (name) => {
  await sleep(500);
  const { data } = await send("Page.captureScreenshot", { format: "png", captureBeyondViewport: true });
  writeFileSync(new URL(`${name}.png`, docs), Buffer.from(data, "base64"));
  console.log(`  saved docs/${name}.png`);
};
const signIn = async (user) => {
  await go("/login");
  if (!(await evaluate(`(() => { const f = [...document.forms].find(f => f.user?.value === ${JSON.stringify(user)}); if (!f) return false; f.submit(); return true; })()`)))
    throw new Error(`No demo user ${user}`);
  await waitFor("location.pathname === '/'", "sign-in");
};
const signOut = async () => { await evaluate("document.querySelector('form[action=\"/account/logout\"]').submit()"); await waitFor("location.pathname === '/login'", "sign-out"); };

try {
  await send("Page.enable");
  await send("Runtime.enable");
  await send("Log.enable");
  await send("Emulation.setDeviceMetricsOverride", { width: 1360, height: 900, deviceScaleFactor: 1, mobile: false });

  console.log("Login");
  await go("/login");
  await shot("login");

  console.log("Dashboard as the SOC analyst");
  await signIn("sam.soc");
  await interactive();
  await waitFor("document.querySelectorAll('table.grid tbody tr').length >= 3", "incident rows");
  if (!(await evaluate("document.querySelector('.tile.alarm') !== null"))) throw new Error("Expected an overdue report on the dashboard");
  await shot("dashboard");

  console.log("Imported LogSentinel incident: assess and classify");
  const fraudLink = await evaluate("[...document.querySelectorAll('table.grid a')].find(a => a.closest('tr').textContent.includes('phishing'))?.getAttribute('href')");
  await go(fraudLink);
  await interactive();
  await waitFor("document.querySelector('.classification') !== null", "classification panel");
  // tick "malicious unauthorised access": the preview must switch to major without saving
  await clickText("label.check", "Successful malicious unauthorised access");
  await waitFor("document.querySelector('.classification .verdict')?.textContent.includes('Major')", "live preview to show major");
  await clickText("label.check", "Affects a financial service");
  await shot("classification");
  await clickText("button", "Save and confirm classification");
  await waitFor("document.querySelector('.notice')?.textContent.includes('Classification confirmed')", "classification to be recorded");
  await waitFor("document.querySelector('.deadline') !== null", "reporting tab with deadlines");
  await clickText("button", "Prepare initial report");
  await waitFor("document.querySelector('.notice')?.textContent.includes('ready for an officer')", "the prepared report");
  await shot("reporting");

  console.log("Evidence and timeline");
  await clickText(".tabs button", "Evidence");
  await waitFor("document.querySelectorAll('.sev').length >= 3", "alert evidence");
  await shot("evidence");

  console.log("Four-eyes: the officer submits");
  const incidentPath = await evaluate("location.pathname");
  await signOut();
  await signIn("iris.isb");
  await go(incidentPath);
  await interactive();
  await clickText(".tabs button", "Reporting");
  await waitFor("[...document.querySelectorAll('button')].some(b => b.textContent.includes('Mark as submitted'))", "submit button for the officer");
  await evaluate("(() => { const i = document.querySelector('.submit input'); i.value = 'MVP-2026-120771'; i.dispatchEvent(new Event('change', { bubbles: true })); })()");
  await sleep(300);
  await clickText("button", "Mark as submitted");
  await waitFor("document.querySelector('.notice')?.textContent.includes('Submission recorded')", "submission");
  await clickText(".tabs button", "Timeline");
  await waitFor("document.querySelectorAll('.timeline li').length >= 4", "timeline entries");
  await shot("timeline");

  console.log("Overdue incident and recurring incidents");
  await go("/");
  await interactive();
  const overdue = await evaluate("[...document.querySelectorAll('table.grid tr')].find(r => r.querySelector('.deadline.overdue'))?.querySelector('a')?.getAttribute('href')");
  await go(overdue);
  await interactive();
  await clickText(".tabs button", "Reporting");
  await waitFor("document.querySelector('.deadline.overdue') !== null", "overdue badge");
  await shot("overdue");
  await go("/recurring");
  await interactive();
  await waitFor("document.querySelector('.classification.major') !== null", "a recurring group");
  await shot("recurring");

  console.log("Narrow dashboard (portfolio card, small screens)");
  await send("Emulation.setDeviceMetricsOverride", { width: 1040, height: 760, deviceScaleFactor: 1, mobile: false });
  await go("/");
  await interactive();
  await send("Page.captureScreenshot", { format: "png" }).then(({ data }) => writeFileSync(new URL("dashboard-narrow.png", docs), Buffer.from(data, "base64")));
  console.log("  saved docs/dashboard-narrow.png");
  await send("Emulation.setDeviceMetricsOverride", { width: 1360, height: 900, deviceScaleFactor: 1, mobile: false });

  console.log("Read-only auditor");
  await signOut();
  await signIn("ana.audit");
  await go(incidentPath);
  await interactive();
  await waitFor("document.querySelector('.classification') !== null", "classification panel");
  if (await evaluate("[...document.querySelectorAll('button')].some(b => b.textContent.includes('Save'))")) throw new Error("The auditor must not see save buttons");

  if (problems.length) throw new Error("Browser errors:\n" + problems.join("\n"));
  console.log("\nAll screens work, no console errors or CSP violations.");
} catch (e) {
  console.error(e.message);
  if (problems.length) console.error("Browser errors:\n" + problems.join("\n"));
  process.exitCode = 1;
} finally {
  ws.close();
  proc.kill();
}
