import { Monitor, Moon, Sun } from "lucide-react";
import { useTheme, type ThemePreference } from "../theme";
import { Segmented } from "./ui";

export function ThemeSwitcher({ compact }: { compact?: boolean }) {
  const { preference, setPreference } = useTheme();
  const options: { value: ThemePreference; label: string; icon: React.ReactNode }[] = [
    { value: "light", label: "Chiaro", icon: <Sun className="size-3.5" /> },
    { value: "dark", label: "Scuro", icon: <Moon className="size-3.5" /> },
    { value: "system", label: "Sistema", icon: <Monitor className="size-3.5" /> },
  ];

  return (
    <Segmented
      value={preference}
      onChange={setPreference}
      options={options.map((o) => ({
        value: o.value,
        label: compact ? <span title={o.label} aria-label={o.label}>{o.icon}</span> : <>{o.icon}{o.label}</>,
      }))}
    />
  );
}
