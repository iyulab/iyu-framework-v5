import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The library's build (Iyu.UBoard.csproj) runs this and embeds what it writes. The output is not committed: it goes under the
// project's `obj` folder, which is not a source folder.
//
// `base: './'` keeps every address in the page relative. The host chooses where the screen is mounted, so the server writes a
// <base> element into the page and the files are found wherever that is.
export default defineConfig({
  base: './',
  plugins: [react()],
  build: { outDir: '../obj/uboard-web', emptyOutDir: true },
});
