// Minimal global toast store + component.
export type ToastType = 'success' | 'error' | 'info'
type Toast = { id: number; message: string; type: ToastType }

let toasts: Toast[] = []
let nextId = 1
const listeners = new Set<(t: Toast[]) => void>()

export function toast(message: string, type: ToastType = 'success', ttl = 4200) {
  const t = { id: nextId++, message, type }
  toasts = [...toasts, t]
  listeners.forEach(l => l(toasts))
  setTimeout(() => dismiss(t.id), ttl)
}
export function dismiss(id: number) {
  if (!toasts.some(x => x.id === id)) return
  toasts = toasts.filter(x => x.id !== id)
  listeners.forEach(l => l(toasts))
}
export function subscribeToast(cb: (t: Toast[]) => void) {
  listeners.add(cb)
  cb(toasts)
  return () => { listeners.delete(cb) }
}
