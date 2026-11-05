import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// In development (npm run dev) the worlds are still served by server.mjs: start it separately with "npm start".
const toServer = {
  target: 'http://127.0.0.1:5173',
  changeOrigin: true,
  configure: (proxy) => proxy.on('proxyReq', (request) => request.removeHeader('origin')),
};

export default defineConfig({
  plugins: [react()],
  server: { port: 5175, proxy: { '/api': toServer, '/maps': toServer, '/thumb': toServer, '/uploads': toServer } },
});
