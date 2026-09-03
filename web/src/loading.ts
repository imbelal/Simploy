// Simple global request-in-flight counter + subscription, so the UI can show a
// loading indicator whenever an API call is in progress.
let count = 0
const listeners = new Set<(n: number) => void>()

export function trackLoading() { count++; listeners.forEach(l => l(count)) }
export function untrackLoading() { count = Math.max(0, count - 1); listeners.forEach(l => l(count)) }
export function onLoadingChange(cb: (n: number) => void) {
  listeners.add(cb)
  return () => { listeners.delete(cb) }
}
