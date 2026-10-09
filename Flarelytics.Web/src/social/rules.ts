import type { NetworkLimits, SocialAccount, SocialMediaItem, SocialNetwork } from "../api/types";

/*
 * Gli stessi conti di SocialRules sul server: qui servono per i contatori
 * mentre si scrive, là per decidere. Se cambiano, vanno cambiati insieme.
 */

const url = /https?:\/\/[^\s]+/g;
const hashtag = /(?<![\p{L}\p{N}_])#[\p{L}\p{N}_]+/gu;
const segmenter = typeof Intl !== "undefined" && "Segmenter" in Intl ? new Intl.Segmenter("it", { granularity: "grapheme" }) : null;

export function countCharacters(text: string, limits: NetworkLimits): number {
  switch (limits.characterCounting) {
    case "graphemes":
      return segmenter ? [...segmenter.segment(text)].length : [...text].length;
    case "mastodon":
      return [...text.replace(url, "x".repeat(23))].length;
    default:
      return [...text].length;
  }
}

/** Quello che impedirebbe di pubblicare su quella rete, in frasi brevi. */
export function problems(text: string, media: SocialMediaItem[], limits: NetworkLimits): string[] {
  const out: string[] = [];
  const count = countCharacters(text, limits);
  if (count > limits.maxCharacters) out.push(`${count - limits.maxCharacters} caratteri di troppo`);
  if (!text.trim() && media.length === 0) out.push("post vuoto");
  if (limits.requiresMedia && media.length === 0) out.push("serve almeno un'immagine");
  if (media.length > limits.maxImages) out.push(`al massimo ${limits.maxImages} immagini`);
  if (limits.maxHashtags !== null && (text.match(hashtag)?.length ?? 0) > limits.maxHashtags) out.push(`al massimo ${limits.maxHashtags} hashtag`);
  media.forEach((m, i) => {
    if (m.sizeBytes > limits.maxImageBytes) out.push(`immagine ${i + 1} troppo pesante`);
    const ratio = m.width / m.height;
    if ((limits.minAspectRatio !== null && ratio < limits.minAspectRatio * 0.99) || (limits.maxAspectRatio !== null && ratio > limits.maxAspectRatio * 1.01))
      out.push(`immagine ${i + 1}: proporzioni fuori da 4:5 – 1,91:1`);
  });
  return out;
}

export const networkName: Record<SocialNetwork, string> = {
  Bluesky: "Bluesky",
  Mastodon: "Mastodon",
  Instagram: "Instagram",
  FacebookPage: "Facebook",
};

export const accountLabel = (a: Pick<SocialAccount, "handle" | "name">) => a.handle ?? a.name;
