import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// In sviluppo l'API si raggiunge attraverso il proxy di Vite, così pagina e
// API stanno sulla stessa origine come in produzione dietro Caddy. È quello
// che permette al cookie di sessione (SameSite=Strict, path /api/v1/auth) di
// funzionare anche in locale.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": "http://localhost:5080",
    },
  },
});
