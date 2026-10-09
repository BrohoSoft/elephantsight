import { request } from "../api/client";
import type { SocialMediaItem } from "../api/types";
import { toJpeg } from "./image";

/** Pezzi da 40 MB: sotto i 100 MB che Cloudflare lascia passare per richiesta, e sotto i 50 del server. */
const CHUNK_BYTES = 40 * 1024 * 1024;

const isVideo = (file: File) => file.type.startsWith("video/") || /\.(mp4|mov|m4v)$/i.test(file.name);

/**
 * Carica un file del post. Le immagini si convertono in JPEG qui (vedi
 * image.ts) e partono in una richiesta. I video partono come sono, a pezzi:
 * un video da telefono supera facilmente il tetto di una singola richiesta.
 */
export async function uploadMedia(orgId: string, file: File, onProgress: (fraction: number) => void): Promise<SocialMediaItem> {
  if (!isVideo(file)) {
    const form = new FormData();
    form.append("file", await toJpeg(file), file.name.replace(/\.[^.]+$/, "") + ".jpg");
    const item = await request<SocialMediaItem>(`/orgs/${orgId}/social/media`, { method: "POST", body: form });
    onProgress(1);
    return item;
  }

  const base = `/orgs/${orgId}/social/media/uploads`;
  const { uploadId } = await request<{ uploadId: string }>(base, { method: "POST", body: { fileName: file.name, size: file.size } });
  for (let offset = 0; offset < file.size; offset += CHUNK_BYTES) {
    const chunk = file.slice(offset, Math.min(offset + CHUNK_BYTES, file.size));
    // Un errore di rete si riprova sullo stesso offset: il server non lo aggiunge due volte.
    for (let attempt = 0; ; attempt++) {
      try {
        await request(`${base}/${uploadId}?offset=${offset}`, { method: "PUT", body: chunk });
        break;
      } catch (e) {
        if (attempt === 2) throw e;
      }
    }
    onProgress(Math.min(1, (offset + chunk.size) / file.size));
  }
  return request<SocialMediaItem>(`${base}/${uploadId}/complete`, { method: "POST", body: { fileName: file.name } });
}

export const formatDuration = (ms: number) => {
  const s = Math.round(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};
