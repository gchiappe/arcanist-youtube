import { defineConfig } from "vite";
import preact from "@preact/preset-vite";

// Served by the extension at /ext/youtube/: relative asset paths, built into the extension's wwwroot.
export default defineConfig({
  base: "./",
  plugins: [preact()],
  build: {
    outDir: "../src/Arcanist.YouTube/wwwroot",
    emptyOutDir: true,
    target: "es2022",
  },
  server: {
    // `npm run dev` against a running Arcanist (5263 is `dotnet run`'s port).
    proxy: {
      "/rest": "http://localhost:5263",
      "/ext/youtube/api": "http://localhost:5263",
    },
  },
});
