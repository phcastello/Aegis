import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { VitePWA } from 'vite-plugin-pwa';

export default defineConfig({
  plugins: [
    vue(),
    VitePWA({
      registerType: 'autoUpdate',
      strategies: 'injectManifest',
      srcDir: 'src',
      filename: 'sw.ts',
      includeAssets: ['favicon.svg', 'icons/icon-192.png', 'icons/icon-512.png'],
      manifest: {
        name: 'Aegis',
        short_name: 'Aegis',
        description: 'v0.5.1',
        theme_color: '#101827',
        background_color: '#101827',
        display: 'standalone',
        lang: 'pt-BR',
        start_url: '/',
        scope: '/',
        icons: [
          {
            src: '/icons/icon-192.png',
            sizes: '192x192',
            type: 'image/png'
          },
          {
            src: '/icons/icon-512.png',
            sizes: '512x512',
            type: 'image/png'
          },
          {
            src: '/icons/icon-512.png',
            sizes: '512x512',
            type: 'image/png',
            purpose: 'maskable'
          },
          {
            src: '/icons/notification-badge.png',
            sizes: '96x96',
            type: 'image/png',
            purpose: 'monochrome'
          }
        ]
      },
      injectManifest: { globPatterns: ['**/*.{js,css,html,svg,png,ico}'] }
    })
  ]
});
