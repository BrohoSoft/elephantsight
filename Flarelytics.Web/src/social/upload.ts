import { request } from "../api/client";
import type { SocialMediaItem } from "../api/types";
import { imageThumbnail, toJpeg, videoThumbnail } from "./image";

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
    const jpeg = await toJpeg(file);
    const form = new FormData();
    form.append("file", jpeg, file.name.replace(/\.[^.]+$/, "") + ".jpg");
    const item = await request<SocialMediaItem>(`/orgs/${orgId}/social/media`, { method: "POST", body: form });
    onProgress(1);
    return withThumbnail(orgId, item, () => imageThumbnail(jpeg));
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
  const video = await request<SocialMediaItem>(`${base}/${uploadId}/complete`, { method: "POST", body: { fileName: file.name } });
  return withThumbnail(orgId, video, () => videoThumbnail(file));
}

/**
 * Manda la miniatura (~400 px) di un file appena caricato: resta quando
 * l'originale si cancella dopo la pubblicazione. Se non riesce (un video che
 * il browser non sa leggere) il file resta valido, solo senza miniatura.
 */
async function withThumbnail(orgId: string, item: SocialMediaItem, make: () => Promise<Blob>): Promise<SocialMediaItem> {
  try {
    return await request<SocialMediaItem>(`/orgs/${orgId}/social/media/${item.id}/thumbnail`, { method: "POST", body: await make() });
  } catch {
    return item;
  }
}

const pendingThumbnails = new Map<string, Promise<string | null>>();

/**
 * La miniatura di un file che non ce l'ha ancora (arrivato con l'API
 * pubblica, o caricato prima delle miniature), fatta la prima volta che il
 * pannello lo mostra, così c'è quando l'originale si cancella. Una sola
 * richiesta per file, anche se lo mostrano più componenti.
 */
export function ensureThumbnail(orgId: string, item: SocialMediaItem): Promise<string | null> {
  if (item.thumbnailUrl || !item.url) return Promise.resolve(item.thumbnailUrl);
  let pending = pendingThumbnails.get(item.id);
  if (!pending) {
    const url = item.url;
    pending = (async () => {
      try {
        const thumbnail = item.kind === "Video" ? await videoThumbnail(url) : await imageThumbnail(await (await fetch(url)).blob());
        const updated = await request<SocialMediaItem>(`/orgs/${orgId}/social/media/${item.id}/thumbnail`, { method: "POST", body: thumbnail });
        return updated.thumbnailUrl;
      } catch {
        return null;
      }
    })();
    pendingThumbnails.set(item.id, pending);
  }
  return pending;
}

export const formatDuration = (ms: number) => {
  const s = Math.round(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};
