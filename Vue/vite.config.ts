import { fileURLToPath, URL } from 'node:url'

import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import vueDevTools from 'vite-plugin-vue-devtools'

// 后端地址,与 Api/DImage.Api/Properties/launchSettings.json 的 applicationUrl 对齐
const backendTarget = 'http://localhost:5180'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue(), vueDevTools()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    // 开发期通过代理实现前后端同源,避免额外配置 CORS
    proxy: {
      '/health': { target: backendTarget, changeOrigin: true },
      '/api': { target: backendTarget, changeOrigin: true },
    },
  },
})
