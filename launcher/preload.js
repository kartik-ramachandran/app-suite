// The only bridge between the window and Node: one call + one event stream.
// The same two functions exist in the phone build (renderer/app.js falls back
// to fetch + EventSource when this preload isn't there).
const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('launcher', {
  call: async (method, ...args) => {
    const res = await ipcRenderer.invoke('launcher:call', method, args)
    if (!res.ok) throw new Error(res.error)
    return res.result
  },
  onEvent: fn => {
    const handler = (_e, msg) => fn(msg)
    ipcRenderer.on('launcher:event', handler)
    return () => ipcRenderer.removeListener('launcher:event', handler)
  },
  desktop: true,
})
