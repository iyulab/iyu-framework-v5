import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The built page goes where the host serves static files from (`../wwwroot`); it is not committed.
export default defineConfig({
  plugins: [react()],
  build: { outDir: '../wwwroot', emptyOutDir: true },
});
