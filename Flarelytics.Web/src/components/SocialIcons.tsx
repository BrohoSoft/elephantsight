import clsx from "clsx";
import type { SocialNetwork } from "../api/types";

/**
 * Come per gli store: segni neutri, non i loghi. Bastano a riconoscere la rete
 * a colpo d'occhio, in un colore solo, in tutti e due i temi.
 */
export function NetworkGlyph({ network, className }: { network: SocialNetwork; className?: string }) {
  const common = { viewBox: "0 0 16 16", className: clsx("size-4 shrink-0", className), fill: "none", stroke: "currentColor", strokeWidth: 1.4, "aria-hidden": true } as const;
  switch (network) {
    case "Bluesky":
      // Una farfalla: due ali.
      return (
        <svg {...common} strokeLinejoin="round">
          <path d="M8 7.2C7 5 4.6 2.8 3 2.6c-1-.1-1.2.8-1 2 .3 1.8 1 3.2 3 3.4-2 .4-2.3 2-1 3.1 1.6 1.4 3 .3 4-2.1Z" />
          <path d="M8 7.2c1-2.2 3.4-4.4 5-4.6 1-.1 1.2.8 1 2-.3 1.8-1 3.2-3 3.4 2 .4 2.3 2 1 3.1-1.6 1.4-3 .3-4-2.1Z" />
        </svg>
      );
    case "Mastodon":
      // Un fumetto con dentro una "m".
      return (
        <svg {...common} strokeLinejoin="round" strokeLinecap="round">
          <path d="M3 3.5C3 2.7 3.7 2 4.5 2h7c.8 0 1.5.7 1.5 1.5v6c0 .8-.7 1.5-1.5 1.5H8l-3 3v-3h-.5C3.7 11 3 10.3 3 9.5Z" />
          <path d="M5.5 8.5V5.5M8 8.5V5.5M10.5 8.5V6c0-.6-.4-.9-.9-.9s-.8.3-.8.9" />
        </svg>
      );
    case "Instagram":
      // Una fotocamera quadrata.
      return (
        <svg {...common}>
          <rect x="2" y="2" width="12" height="12" rx="3.5" />
          <circle cx="8" cy="8" r="2.6" />
          <circle cx="11.6" cy="4.4" r=".5" fill="currentColor" stroke="none" />
        </svg>
      );
    case "TikTok":
      // Una nota musicale.
      return (
        <svg {...common} strokeLinecap="round" strokeLinejoin="round">
          <path d="M9 2.5v8a2.5 2.5 0 1 1-2.5-2.5" />
          <path d="M9 2.5c.4 1.8 1.7 3 3.5 3.2" />
        </svg>
      );
    case "FacebookPage":
      // Una "f" in un riquadro.
      return (
        <svg {...common} strokeLinecap="round">
          <rect x="2" y="2" width="12" height="12" rx="3" />
          <path d="M10.2 5H9.4C8.6 5 8 5.6 8 6.4V14M6.3 8.3h3.6" />
        </svg>
      );
  }
}
