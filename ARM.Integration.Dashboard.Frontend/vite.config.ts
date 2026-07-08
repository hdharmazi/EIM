import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "node:path";

// The .NET API the SPA talks to. In dev, requests to /api are proxied here so
// the app can use same-origin relative paths (in production nginx proxies /api).
const apiTarget = process.env.VITE_DEV_API_URL ?? "http://localhost:5060";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    assetsDir: "assets",
  },
  server: {
    port: 5173,
    proxy: {
      "/api": { target: apiTarget, changeOrigin: true, secure: false },
    },
  },
});
