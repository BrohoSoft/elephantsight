import type { ReleaseStage } from "../api/types";
import { Badge } from "./ui";

const STAGES: Record<ReleaseStage, { label: string; tone: "ok" | "warn" | "bad" | "neutral" | "brand" }> = {
  Live: { label: "Pubblicata", tone: "ok" },
  Rolling: { label: "In distribuzione", tone: "brand" },
  InReview: { label: "In revisione", tone: "warn" },
  Draft: { label: "In preparazione", tone: "neutral" },
  Rejected: { label: "Rifiutata", tone: "bad" },
  Retired: { label: "Superata", tone: "neutral" },
  Processing: { label: "In elaborazione", tone: "warn" },
  Other: { label: "Altro", tone: "neutral" },
};

/** Lo stato di una versione o di una build, con le stesse parole per i due store; quello originale nel tooltip. */
export function StageBadge({ stage, raw }: { stage: ReleaseStage; raw?: string }) {
  const s = STAGES[stage];
  return (
    <span title={raw}>
      <Badge tone={s.tone}>{s.label}</Badge>
    </span>
  );
}

export const trackLabel = (track: string | null) =>
  ({ production: "Produzione", beta: "Test aperto", alpha: "Test chiuso", internal: "Test interno" } as Record<string, string>)[track ?? ""] ?? track ?? "";
