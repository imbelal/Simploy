import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api'
import { Badge, Button, PageHeader, Panel, StatCard } from '../components/Field'

type Step = { n: number; title: string; desc: string; to: string; cta: string; tech: string }

export default function Dashboard() {
  const [stats, setStats] = useState({ servers: 0, projects: 0, envs: 0, deps: 0 })
  const [busy, setBusy] = useState('')
  const [msg, setMsg] = useState('')

  useEffect(() => {
    Promise.all([
      api.servers.list().catch(() => []),
      api.projects.list().catch(() => []),
      api.envs.list().catch(() => []),
      api.deployments.list().catch(() => []),
    ]).then(([s, p, e, d]) => setStats({ servers: s.length, projects: p.length, envs: e.length, deps: d.length }))
  }, [])

  const runAll = async () => {
    if (!confirm('Re-deploy every environment and re-provision every database? This rebuilds/runs all your recorded resources.')) return
    setBusy('runAll'); setMsg('Running all resources…')
    try {
      const [d, b] = await Promise.all([api.deployments.runAll(), api.databases.runAll()])
      setMsg(`Queued ${d.queued} deployments + ${b.queued} databases`)
    } catch (e: any) { setMsg(e.message) }
    setBusy('')
  }
  const restartAll = async () => {
    if (!confirm('Restart all running app containers (no rebuild)? Control plane stays up.')) return
    setBusy('restart'); setMsg('Restarting containers…')
    try { const r: any = await api.servers.restartContainers(); setMsg(`Restarted ${r.restarted} containers`) }
    catch (e: any) { setMsg(e.message) }
    setBusy('')
  }

  const steps: Step[] = [
    { n: 1, title: 'Install the Agent on a VM', desc: 'The agent is a small service that runs on the server hosting your apps. It pulls your Git repo, builds the Docker image and runs it with docker compose.', to: '/vm-guide', cta: 'Show me how', tech: 'curl | bash · needs Docker' },
    { n: 2, title: 'Add that VM as a Server', desc: 'Register the VM so Simploy knows where to deploy. It checks the Agent on port 8089 to confirm it is online.', to: '/servers', cta: 'Add a server', tech: 'host + IP' },
    { n: 3, title: 'Add your app as a Project', desc: 'Point at your Git repo (public or private). Simploy reads the Dockerfile and builds the image on your servers.', to: '/projects', cta: 'Add a project', tech: 'git repo + Dockerfile' },
    { n: 4, title: 'Create an Environment & Deploy', desc: 'Environments map a project to a server (staging + prod). Hit Deploy and Simploy clones, builds, and runs it.', to: '/deployments', cta: 'Deploy', tech: 'staging / prod' },
  ]

  const done = [stats.servers > 0, stats.projects > 0, stats.envs > 0, stats.deps > 0]
  const nextIdx = done.findIndex(d => !d)

  const StepCard = ({ s, idx }: { s: Step; idx: number }) => {
    const isDone = done[idx]
    const isNext = idx === nextIdx
    return (
      <div className={`rounded-2xl border p-5 flex flex-col transition-all duration-200 ${isNext ? 'border-indigo-300 bg-gradient-to-b from-indigo-50/80 to-white shadow-card-hover ring-1 ring-indigo-200' : 'bg-white border-slate-200/70 shadow-card hover:shadow-card-hover'}`}>
        <div className="flex items-center justify-between">
          <div className={`w-9 h-9 rounded-xl flex items-center justify-center font-bold text-sm shrink-0 ${isDone ? 'bg-emerald-500 text-white' : isNext ? 'bg-gradient-to-br from-indigo-500 to-sky-500 text-white shadow-md shadow-indigo-500/30' : 'bg-slate-100 text-slate-500'}`}>
            {isDone ? '✓' : s.n}
          </div>
          <div className="flex gap-2">
            {isDone && <Badge tone="ok">Done</Badge>}
            {isNext && <Badge tone="violet">Next up</Badge>}
          </div>
        </div>
        <div className="font-semibold text-slate-900 mt-3">{s.title}</div>
        <div className="text-sm text-slate-500 mt-1 flex-1 leading-relaxed">{s.desc}</div>
        <div className="text-[11px] text-slate-400 mt-3 font-mono">{s.tech}</div>
        <Link to={s.to} className="mt-4">
          <Button variant={isNext ? 'primary' : 'secondary'} size="sm" className="w-full">
            {isDone ? 'Revisit' : s.cta} →
          </Button>
        </Link>
      </div>
    )
  }

  return (
    <div className="space-y-8">
      <PageHeader
        title={<span>Deploy apps from Git to <span className="text-gradient">your own servers</span></span>}
        desc="Simploy installs a small Agent on each VM. Give it a Git repo + Dockerfile, and it builds and runs your app there — no external CI/CD needed."
      />

      <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
        <StatCard label="Servers" value={stats.servers} sub="VMs with the Agent" icon="🖥️" />
        <StatCard label="Projects" value={stats.projects} sub="apps (git repos)" icon="📦" />
        <StatCard label="Environments" value={stats.envs} sub="staging / prod" icon="🌍" />
        <StatCard label="Deployments" value={stats.deps} sub="builds so far" icon="🚀" />
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div className="bg-white rounded-2xl border border-slate-200/70 shadow-card p-5 flex items-start gap-4">
          <div className="w-10 h-10 rounded-xl bg-gradient-to-br from-indigo-500 to-sky-500 text-white flex items-center justify-center shrink-0">⚡</div>
          <div className="min-w-0 flex-1">
            <div className="font-semibold text-slate-900">Run all resources</div>
            <div className="text-xs text-slate-500 mt-1">Re-deploy every environment (rebuild) + re-provision every database. Brings back all recorded apps/DBs after a reset.</div>
            <Button variant="primary" size="sm" className="mt-3" onClick={runAll} disabled={busy !== ''}>{busy === 'runAll' ? 'Running…' : 'Run all resources'}</Button>
          </div>
        </div>
        <div className="bg-white rounded-2xl border border-slate-200/70 shadow-card p-5 flex items-start gap-4">
          <div className="w-10 h-10 rounded-xl bg-gradient-to-br from-emerald-500 to-teal-500 text-white flex items-center justify-center shrink-0">↻</div>
          <div className="min-w-0 flex-1">
            <div className="font-semibold text-slate-900">Restart containers</div>
            <div className="text-xs text-slate-500 mt-1">Restart all running app containers (no rebuild) on every server. Control plane stays up — quick recovery if an app is stuck.</div>
            <Button variant="secondary" size="sm" className="mt-3" onClick={restartAll} disabled={busy !== ''}>{busy === 'restart' ? 'Restarting…' : 'Restart containers'}</Button>
          </div>
        </div>
      </div>
      {msg && <div className="text-xs text-slate-600 font-mono">{msg}</div>}

      <div>
        <div className="flex items-center gap-2 mb-3">
          <h2 className="font-semibold text-slate-900">How to deploy your first app</h2>
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
          {steps.map((s, i) => <StepCard key={s.n} s={s} idx={i} />)}
        </div>
      </div>

      <Panel>
        <div className="flex items-start gap-3">
          <div className="w-9 h-9 rounded-xl bg-emerald-100 text-emerald-600 flex items-center justify-center shrink-0">✦</div>
          <div className="text-sm text-emerald-900 leading-relaxed">
            <div className="font-semibold">Demo data is pre-loaded</div>
            Two servers (<code className="bg-emerald-50 px-1.5 rounded font-mono text-xs">127.0.0.1</code>), a sample project, two environments and one deployment are already there.
            Try deploying — it will fail locally (no Agent on your machine), which is expected. To deploy for real, <Link to="/servers" className="underline font-medium">add your actual server</Link> and point the project at your Git repo.
          </div>
        </div>
      </Panel>

      <div>
        <h2 className="font-semibold text-slate-900 mb-3">What runs where</h2>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          {[
            { t: 'Control plane', d: 'OpenAPI :5000 · React UI :5173 · stores projects & servers. Runs on your machine or a small VM.', icon: '🎛️' },
            { t: 'Agent on each server', d: ':8089 · receives deploy jobs, clones Git, runs docker build + compose up. Needs Docker + the socket.', icon: '⚙️' },
            { t: 'Your app', d: 'Containers on the server, built from your Git repo by the Agent, optionally behind Caddy.', icon: '📦' },
          ].map(x => (
            <div key={x.t} className="bg-white rounded-2xl border border-slate-200/70 shadow-card p-5 transition hover:shadow-card-hover">
              <div className="w-9 h-9 rounded-xl bg-gradient-to-br from-indigo-50 to-sky-50 border border-indigo-100 text-center text-lg flex items-center justify-center">{x.icon}</div>
              <div className="font-semibold text-slate-900 mt-3 text-sm">{x.t}</div>
              <div className="text-xs text-slate-500 mt-1 leading-relaxed">{x.d}</div>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
