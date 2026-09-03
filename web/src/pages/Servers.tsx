import { useEffect, useState } from 'react'
import { api } from '../api'
import { toast } from '../toast'
import { Badge, Button, Card, EmptyState, Field, PageHeader, Panel, Skeleton, inputCls } from '../components/Field'

function ServerStatus({ status }: { status: number | string }) {
  const m: Record<number | string, 'pending' | 'ok' | 'slate' | 'red'> = {
    0: 'pending', Pending: 'pending',
    1: 'ok', Online: 'ok',
    2: 'slate', Offline: 'slate',
    3: 'red', Unreachable: 'red',
  }
  const label = typeof status === 'number' ? ['Pending', 'Online', 'Offline', 'Unreachable'][status] : status
  return <Badge tone={m[status] || 'slate'}>{label}</Badge>
}

export default function Servers() {
  const [servers, setServers] = useState<any[]>([])
  const [form, setForm] = useState({ name: '', host: '', sshPort: 22, sshUser: 'root' })
  const [busy, setBusy] = useState(false)
  const [loaded, setLoaded] = useState(false)

  const load = () => api.servers.list().then(s => { setServers(s); setLoaded(true) }).catch(e => toast(e.message, 'error'))
  useEffect(() => { load() }, [])

  const create = async () => {
    if (!form.name || !form.host) return toast('Name and Host are required', 'error')
    setBusy(true)
    try { await api.servers.create(form); setForm({ name: '', host: '', sshPort: 22, sshUser: 'root' }); toast('Server added'); load() }
    catch (e: any) { toast(e.message, 'error') } finally { setBusy(false) }
  }
  const check = async (id: string) => {
    try { const r: any = await api.servers.check(id); toast(`Check: ${JSON.stringify(r)}`, 'info'); load() }
    catch (e: any) { toast(e.message, 'error') }
  }
  const del = async (id: string) => {
    if (!confirm('Delete server? Environments using it will need reassignment.')) return
    try { await api.servers.del(id); toast('Server deleted'); load() }
    catch (e: any) { toast(e.message, 'error') }
  }

  return (
    <div className="space-y-6">
      <PageHeader title="Servers" desc="Each server is a VM that runs the Simploy Agent on port 8089. The Agent builds and runs your apps there." />

      <Panel>
        <div className="font-semibold text-slate-900 mb-4">Add a server</div>
        <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
          <Field label="Name" hint="A label you'll recognise, e.g. prod-vm">
            <input className={inputCls} placeholder="prod-vm" value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} />
          </Field>
          <Field label="Host / IP" hint="Where the Agent is reachable, e.g. 65.108.1.10">
            <input className={inputCls} placeholder="65.108.x.x" value={form.host} onChange={e => setForm({ ...form, host: e.target.value })} />
          </Field>
          <Field label="SSH port">
            <input className={inputCls} type="number" value={form.sshPort} onChange={e => setForm({ ...form, sshPort: +e.target.value })} />
          </Field>
          <Field label="SSH user">
            <input className={inputCls} placeholder="root" value={form.sshUser} onChange={e => setForm({ ...form, sshUser: e.target.value })} />
          </Field>
        </div>
        <div className="flex items-center gap-3 mt-4">
          <Button variant="primary" onClick={create} loading={busy}>Add server</Button>
        </div>
      </Panel>

      <Card title={`Servers (${servers.length})`} subtitle="Green = Agent reachable on :8089">
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-slate-500"><tr><th className="text-left px-5 py-3 font-medium">Name</th><th className="text-left px-4 py-3 font-medium">Host</th><th className="text-left px-4 py-3 font-medium">Status</th><th className="text-left px-4 py-3 font-medium">Actions</th></tr></thead>
          <tbody>
            {!loaded ? (
              <tr><td colSpan={4} className="px-4 py-6"><div className="space-y-3">{Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-4 w-3/4" />)}</div></td></tr>
            ) : servers.map(s => (
              <tr key={s.id} className="border-t border-slate-100 hover:bg-slate-50/60 transition">
                <td className="px-5 py-3 font-medium text-slate-800">{s.name}</td>
                <td className="px-4 py-3 font-mono text-xs text-slate-600">{s.host}:{s.sshPort} <span className="text-slate-400">({s.sshUser})</span></td>
                <td className="px-4 py-3"><ServerStatus status={s.status} /></td>
                <td className="px-4 py-3 flex gap-2">
                  <Button size="sm" variant="secondary" onClick={() => check(s.id)}>Check :8089</Button>
                  <Button size="sm" variant="danger" onClick={() => del(s.id)}>Delete</Button>
                </td>
              </tr>
            ))}
            {loaded && servers.length === 0 && <tr><td colSpan={4}><EmptyState title="No servers yet">Add one above, or run the one-liner install on your VM first.</EmptyState></td></tr>}
          </tbody>
        </table>
      </Card>
    </div>
  )
}
