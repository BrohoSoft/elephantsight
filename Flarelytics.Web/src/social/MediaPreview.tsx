import clsx from "clsx";
import { ImageOff, Video } from "lucide-react";
import { useEffect, useState } from "react";
import { canAdmin } from "../api/hooks";
import type { SocialMediaItem } from "../api/types";
import { useOrg } from "../components/org";
import { ensureThumbnail, formatDuration } from "./upload";

/**
 * La miniatura di un file del post: per gli elenchi (coda, ricorrenti) e
 * l'editor. Preferisce la miniatura (~400 px, leggera, e l'unica cosa che
 * resta quando l'originale si cancella dopo la pubblicazione); un video senza
 * miniatura mostra il primo fotogramma. Chi può modificare i post crea la
 * miniatura mancante la prima volta che la vede.
 */
export function MediaPreview({ item, className, player = false }: {
  item: SocialMediaItem;
  className?: string;
  /** Nell'editor: un video ancora disponibile si guarda con i controlli. */
  player?: boolean;
}) {
  const org = useOrg();
  const [thumbnail, setThumbnail] = useState(item.thumbnailUrl);
  const admin = canAdmin(org.role);

  useEffect(() => {
    setThumbnail(item.thumbnailUrl);
    if (item.thumbnailUrl || !item.url || !admin) return;
    let live = true;
    ensureThumbnail(org.id, item).then((url) => live && url && setThumbnail(url));
    return () => {
      live = false;
    };
  }, [item, org.id, admin]);

  if (item.kind === "Video" && item.url && (player || !thumbnail)) {
    return <video src={player ? item.url : `${item.url}#t=0.1`} poster={thumbnail ?? undefined} controls={player} muted={!player} preload="metadata"
      className={clsx("bg-black object-cover", className)} />;
  }
  const src = thumbnail ?? item.url;
  if (!src) {
    return (
      <div className={clsx("flex items-center justify-center bg-panel-2 text-faint", className)} title="Originale cancellato dopo la pubblicazione">
        <ImageOff className="size-4" />
      </div>
    );
  }
  return <img src={src} alt={item.altText ?? ""} loading="lazy" className={clsx("object-cover", className)} />;
}

/** La durata sopra la miniatura di un video. */
export function VideoBadge({ item }: { item: SocialMediaItem }) {
  return (
    <span className="absolute inset-x-0 bottom-0 flex items-center gap-0.5 bg-black/60 px-1 text-[0.625rem] text-white">
      <Video className="size-2.5" />{formatDuration(item.durationMs ?? 0)}
    </span>
  );
}
