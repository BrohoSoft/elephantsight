/*
 * Le immagini dei post si convertono qui, nel browser, prima del caricamento:
 * Instagram accetta solo JPEG e Bluesky meno di 1.000.000 di byte. Così il
 * server riceve sempre un JPEG già giusto per tutte le reti e non ha bisogno
 * di librerie grafiche.
 */

/** Il lato lungo: oltre, nessuna rete mostra l'immagine più grande. */
const MAX_SIDE = 2048;
/** Sotto il limite di Bluesky, con un po' di margine. */
const MAX_BYTES = 976_000;

export async function toJpeg(file: File): Promise<Blob> {
  // L'orientamento EXIF va applicato adesso: il JPEG che esce non avrà più l'EXIF.
  const bitmap = await createImageBitmap(file, { imageOrientation: "from-image" });
  try {
    let scale = Math.min(1, MAX_SIDE / Math.max(bitmap.width, bitmap.height));
    for (let attempt = 0; attempt < 6; attempt++) {
      const canvas = document.createElement("canvas");
      canvas.width = Math.max(1, Math.round(bitmap.width * scale));
      canvas.height = Math.max(1, Math.round(bitmap.height * scale));
      const ctx = canvas.getContext("2d")!;
      // Un PNG trasparente in JPEG diventerebbe nero: meglio bianco.
      ctx.fillStyle = "#fff";
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);

      for (const quality of [0.9, 0.82, 0.74, 0.66]) {
        const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, "image/jpeg", quality));
        if (blob && blob.size <= MAX_BYTES) return blob;
      }
      scale *= 0.8;
    }
    throw new Error("Non riesco a ridurre l'immagine sotto 1 MB.");
  } finally {
    bitmap.close();
  }
}
