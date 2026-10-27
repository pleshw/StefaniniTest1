import { defineConfig } from 'vite';

// Multi-page build: each page is its own entry, so admin code is never shipped to the Shop page.
export default defineConfig({
  build: {
    rolldownOptions: {
      input: { index: 'index.html', shop: 'shop.html', admin: 'admin.html' },
    },
  },
  server: {
    // `npm run dev` talks to the BFF of a running compose stack through nginx.
    proxy: { '/bff': 'http://localhost:8080' },
  },
});
