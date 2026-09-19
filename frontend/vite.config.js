import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    // Allow access through ngrok/cloudflare tunnels (their subdomains rotate).
    // A leading dot matches every subdomain of that domain.
    allowedHosts: ['.ngrok-free.dev', '.ngrok-free.app', '.ngrok.io', '.trycloudflare.com'],
    proxy: {
      // The browser calls the tunnel host at /api/*, and Vite forwards it here
      // server-side to the backend — so the API works over the same tunnel with no CORS.
      '/api': {
        // Overridable so a second dev server can be pointed at an API running on another port
        // (a release build, a branch) without editing this file: VITE_API_PROXY=http://localhost:5197
        target: process.env.VITE_API_PROXY || 'http://localhost:5200',
        changeOrigin: true,
      },
    },
  },
})
