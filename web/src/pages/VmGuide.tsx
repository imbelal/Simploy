import { Badge, PageHeader, Panel } from '../components/Field'

function Code({ children }: { children: string }) {
  return (
    <pre className="bg-slate-900 text-slate-100 rounded-xl p-4 text-xs overflow-auto font-mono leading-relaxed shadow-inner">{children}</pre>
  )
}

function Option({ n, title, active, children }: { n: string; title: string; active?: boolean; children: React.ReactNode }) {
  return (
    <Panel>
      <div className="flex items-center gap-2.5 mb-3">
        <span className="w-7 h-7 rounded-lg bg-gradient-to-br from-indigo-500 to-sky-500 text-white flex items-center justify-center text-xs font-bold">{n}</span>
        <span className="font-semibold text-slate-900">{title}</span>
        {active && <Badge tone="ok">Recommended</Badge>}
      </div>
      <div className="space-y-3 text-sm text-slate-600 leading-relaxed">{children}</div>
    </Panel>
  )
}

export default function VmGuide() {
  return (
    <div className="space-y-6">
      <PageHeader title="VM Guide" desc="Three ways to run Simploy: skip the VM entirely for a local demo, or test on a real Linux server (Hetzner, DigitalOcean, etc.)." />

      <Option n="A" title="Local demo — no VM" >
        <ol className="list-none space-y-1.5 text-xs text-slate-600">
          <li>1. Start the API: <code className="bg-slate-100 px-1.5 rounded font-mono">cd src/Simploy.Api && dotnet run</code> → :5000</li>
          <li>2. Start the UI: <code className="bg-slate-100 px-1.5 rounded font-mono">cd web && npm run dev</code> → :5173</li>
          <li>3. Demo data is seeded (2 servers on <code className="bg-slate-100 px-1.5 rounded font-mono">127.0.0.1</code>). Triggering a deploy fails without an Agent — expected.</li>
        </ol>
      </Option>

      <Option n="B" title="Real VM — one-liner install" active>
        <p className="text-xs text-slate-500">Runs everything on one server (control plane + Agent), Dokploy-style.</p>
        <Code>{`# on the VM (Ubuntu/Debian)
sudo apt update && sudo apt install -y git curl

# 1. clone + start control plane (postgres + api:5000 + web:5173)
git clone https://github.com/<you>/Simploy /opt/simploy
cd /opt/simploy && docker compose up -d --build

# 2. install the Agent from the local clone
SIMPLOY_REPO=/opt/simploy bash scripts/install.sh

# 3. open the ports
ufw allow 8089/tcp && ufw allow 5000/tcp`}</Code>
        <p className="text-xs text-slate-500">Then open <code className="bg-slate-100 px-1.5 rounded font-mono text-[11px]">http://&lt;VM_IP&gt;:5173</code> and add a Server with the VM's <b>real IP</b> (not 127.0.0.1).</p>
      </Option>

      <Option n="C" title="Control plane on Mac, Agent on VM">
        <Code>{`# on the VM — install the agent only
git clone https://github.com/<you>/Simploy /opt/simploy
SIMPLOY_REPO=/opt/simploy bash /opt/simploy/scripts/install.sh

# on your Mac — run the API + UI, then add the VM IP as a server
cd src/Simploy.Api && dotnet run        # :5000
cd web && npm run dev                    # :5173`}</Code>
      </Option>

      <Panel>
        <div className="flex items-start gap-3">
          <span className="w-9 h-9 rounded-xl bg-amber-100 text-amber-600 flex items-center justify-center shrink-0">⚠</span>
          <div className="text-sm text-amber-900 leading-relaxed">
            <div className="font-semibold mb-1">Security note</div>
            :8089 (the Agent) is unauthenticated in v1. For internet-facing VMs, put it behind Tailscale/WireGuard, or we add token auth next.
          </div>
        </div>
      </Panel>
    </div>
  )
}
