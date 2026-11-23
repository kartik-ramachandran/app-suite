import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// strictPort: if 5279 is taken, FAIL — a silent fallback port means you test one
// server while the browser refreshes another.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5279,
    strictPort: true,
    proxy: {
      '/api': {
        target: 'http://localhost:5326',
        changeOrigin: true
      }
    }
  }
})
