import { useEffect, useState } from 'react'
import { api } from '../api'
import { toast } from '../toast'
import { Badge, Button, Card, EmptyState, Field, PageHeader, Panel, inputCls } from '../components/Field'

export default function Databases() {
  const [dbs, setDbs] = useState<any[]>([])
  const [servers, setServers] = useState<any[]>([])
  const [form, setForm] = useState({ name: '', type: 'postgres', version: '16', serverId: '', dataPath: '' })
  const [busy, setBusy] = useState(false)

  const load = () => {
    api.databases.list().then(setDbs).catch(e => toast(e.message, 'error'))
    api.servers.list().then(setServers).catch(() => { })
  }
  useEffect(() => { load(); const i = setInterval(load, 3000); return () => clearInterval(i) }, [])

  const create = async () => {
    if (!form.name || !form.serverId) return toast('Name and server are required', 'error')
    setBusy(true)
    try { await api.databases.create(form); toast('Database created — provisioning…'); setForm({ ...form, name: '', version: form.type === 'postgres' ? '16' : form.type === 'mysql' ? '8' : '7' }); load() }
    catch (e: any) { toast(e.message, 'error') } finally { setBusy(false) }
  }
  const del = async (id: string) => { if (!confirm('Delete this database (stops + removes volume)?')) return; try { await api.databases.del(id); toast('Database removed'); load() } catch (e: any) { toast(e.message, 'error') } }

  const connString = (d: any) => `host=db-${d.name}  port=${d.port}  db=${d.databaseName}  user=${d.username}  password=${d.password}`

  return (
    <div className="space-y-6">
      <PageHeader title="Databases" desc="Managed database containers (Postgres / MySQL / Redis / MongoDB) on your servers. Apps on the same proxy network connect by host db-&lt;name&gt;." />

      <Panel>
        <div className="font-semibold text-slate-900 mb-4">Create a database</div>
        <div className="grid grid-cols-2 md:grid-cols-5 gap-4">
          <Field label="Name" hint="Used as the host alias db-<name>">
            <input className={inputCls} value={form.name} onChange={e => setForm({ ...form, name: e.target.value.toLowerCase().replace(/[^a-z0-9]+/g, '-') })} placeholder="mydb" />
          </Field>
          <Field label="Type">
            <select className={inputCls} value={form.type} onChange={e => setForm({ ...form, type: e.target.value, version: e.target.value === 'postgres' ? '16' : e.target.value === 'mysql' ? '8' : e.target.value === 'mssql' ? '2022' : e.target.value === 'redis' ? '7' : '7' })}>
              <option value="postgres">PostgreSQL</option>
              <option value="mysql">MySQL</option>
              <option value="mssql">MSSQL (SQL Server)</option>
              <option value="redis">Redis</option>
              <option value="mongodb">MongoDB</option>
            </select>
          </Field>
          <Field label="Version">
            <input className={inputCls} value={form.version} onChange={e => setForm({ ...form, version: e.target.value })} />
          </Field>
          <Field label="Server" hint="Which VM to run it on.">
            <select className={inputCls} value={form.serverId} onChange={e => setForm({ ...form, serverId: e.target.value })}>
              <option value="">Select server…</option>
              {servers.map(s => <option key={s.id} value={s.id}>{s.name} ({s.host})</option>)}
            </select>
          </Field>
          <Field label="Data path (optional)" hint="Host directory for the data (bind mount). Leave blank for a Docker volume. Use a path under /opt/simploy, e.g. /opt/simploy/data/mydb.">
            <input className={inputCls} placeholder="/opt/simploy/data/mydb" value={form.dataPath} onChange={e => setForm({ ...form, dataPath: e.target.value })} />
          </Field>
        </div>
        <div className="flex items-center gap-3 mt-4">
          <Button variant="primary" onClick={create} loading={busy}>Create database</Button>
        </div>
      </Panel>

      <Card title={`Databases (${dbs.length})`}>
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-slate-500"><tr><th className="text-left px-5 py-3 font-medium">Name</th><th className="text-left px-4 py-3 font-medium">Type</th><th className="text-left px-4 py-3 font-medium">Server</th><th className="text-left px-4 py-3 font-medium">Status</th><th className="text-left px-4 py-3 font-medium">Connect</th><th className="text-left px-4 py-3 font-medium"></th></tr></thead>
          <tbody>
            {dbs.map(d => (
              <tr key={d.id} className="border-t border-slate-100 hover:bg-slate-50/60 transition">
                <td className="px-5 py-3 font-mono text-xs text-slate-800">db-{d.name}</td>
                <td className="px-4 py-3 text-xs">{d.type}:{d.version}</td>
                <td className="px-4 py-3 text-xs">{d.server?.name} ({d.server?.host})</td>
                <td className="px-4 py-3"><Badge tone={d.status === 'Running' ? 'ok' : d.status === 'Failed' ? 'red' : 'pending'}>{d.status}</Badge></td>
                <td className="px-4 py-3"><code className="text-[11px] bg-slate-100 rounded px-1.5 py-0.5 font-mono">{connString(d)}</code></td>
                <td className="px-4 py-3 text-right"><Button size="sm" variant="danger" onClick={() => del(d.id)}>Delete</Button></td>
              </tr>
            ))}
            {dbs.length === 0 && <tr><td colSpan={6}><EmptyState title="No databases yet">Create one above (a password is generated automatically).</EmptyState></td></tr>}
          </tbody>
        </table>
      </Card>
    </div>
  )
}
