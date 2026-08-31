import { BrowserRouter, Routes, Route, NavLink, Navigate } from 'react-router-dom'
import { auth } from './api'
import Dashboard from './pages/Dashboard'
import Servers from './pages/Servers'
import Projects from './pages/Projects'
import Deployments from './pages/Deployments'
import VmGuide from './pages/VmGuide'
import Login from './pages/Login'

const nav = [
  { to: '/', label: 'Dashboard', icon: '🏠', end: true },
  { to: '/servers', label: 'Servers', icon: '🖥️' },
  { to: '/projects', label: 'Projects', icon: '📦' },
  { to: '/deployments', label: 'Deployments', icon: '🚀' },
  { to: '/vm-guide', label: 'VM Guide', icon: '📖' },
]

function Sidebar() {
  return (
    <aside className="w-[260px] shrink-0 flex flex-col text-white bg-gradient-to-b from-slate-900 via-slate-900 to-indigo-950 p-4">
      <div className="flex items-center gap-3 px-2 py-4 mb-2">
        <div className="w-9 h-9 rounded-xl bg-gradient-to-br from-indigo-500 to-sky-500 flex items-center justify-center font-bold text-white shadow-lg shadow-indigo-900/40">S</div>
        <div>
          <div className="font-bold leading-none text-[15px] tracking-tight">Simploy</div>
          <div className="text-xs text-slate-400 mt-0.5">Deploy from Git</div>
        </div>
      </div>

      <nav className="space-y-1 flex-1">
        {nav.map(item => (
          <NavLink
            key={item.to}
            to={item.to}
            end={item.end}
            className={({ isActive }) =>
              `flex items-center gap-3 px-3 py-2.5 rounded-xl text-sm font-medium transition-all duration-200 ${
                isActive
                  ? 'bg-gradient-to-r from-indigo-500/90 to-sky-500/90 text-white shadow-md shadow-indigo-900/30'
                  : 'text-slate-300 hover:text-white hover:bg-white/5'
              }`
            }
          >
            <span className="w-5 text-center text-base leading-none">{item.icon}</span>
            {item.label}
          </NavLink>
        ))}
      </nav>

      <div className="mt-auto p-4 rounded-2xl bg-white/5 border border-white/10 text-xs leading-relaxed text-slate-400">
        <div className="font-semibold text-slate-200 mb-1.5">Your flow</div>
        <ol className="space-y-1 list-decimal list-inside">
          <li>Install Agent on a VM</li>
          <li>Add the server</li>
          <li>Add your app (git repo)</li>
          <li>Create env + deploy</li>
        </ol>
      </div>
    </aside>
  )
}

export default function App() {
  const loggedIn = auth.isLoggedIn()
  return (
    <BrowserRouter>
      {!loggedIn ? (
        <Routes>
          <Route path="/login" element={<Login />} />
          <Route path="*" element={<Navigate replace to="/login" />} />
        </Routes>
      ) : (
        <div className="flex min-h-screen bg-slate-50">
          <Sidebar />
          <div className="flex-1 min-w-0 flex flex-col">
            <header className="h-16 bg-white/70 backdrop-blur border-b border-slate-200/70 flex items-center justify-between px-8 sticky top-0 z-10">
              <div className="text-sm text-slate-500">
                Control plane <a href="http://localhost:5000/health" target="_blank" rel="noreferrer" className="text-indigo-600 hover:underline font-medium">:5000</a>
                <span className="mx-2 text-slate-300">·</span>
                Agent on each server <code className="bg-slate-100 px-1.5 py-0.5 rounded text-xs font-mono text-slate-600">:8089</code>
              </div>
              <div className="flex items-center gap-3">
                <span className="inline-flex items-center gap-1.5 text-xs font-medium text-emerald-700 bg-emerald-50 ring-1 ring-inset ring-emerald-600/20 px-2.5 py-1 rounded-full">
                  <span className="w-1.5 h-1.5 rounded-full bg-emerald-500 animate-pulse-soft" />
                  Control plane running
                </span>
                <button onClick={auth.logout} className="text-xs bg-slate-900 text-white px-3 py-1.5 rounded-full hover:bg-black">Sign out</button>
              </div>
            </header>

            <main className="flex-1 px-8 py-8 max-w-[1200px] w-full mx-auto">
              {/* soft gradient glow behind content */}
              <div className="pointer-events-none fixed inset-0 -z-10 bg-[radial-gradient(1200px_400px_at_50%_-80px,rgba(99,102,241,0.10),transparent)]" />
              <div className="animate-fade-up">
                <Routes>
                  <Route path="/" element={<Dashboard />} />
                  <Route path="/servers" element={<Servers />} />
                  <Route path="/projects" element={<Projects />} />
                  <Route path="/deployments" element={<Deployments />} />
                  <Route path="/vm-guide" element={<VmGuide />} />
                  <Route path="/login" element={<Navigate replace to="/" />} />
                </Routes>
              </div>
            </main>
          </div>
        </div>
      )}
    </BrowserRouter>
  )
}
