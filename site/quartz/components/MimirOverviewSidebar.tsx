import {
  QuartzComponent,
  QuartzComponentConstructor,
  QuartzComponentProps,
} from "./types";
import { FullSlug, resolveRelative } from "../util/path";

type SidebarLink = {
  label: string;
  slug: FullSlug;
};

const links: SidebarLink[] = [
  { label: "Current System Map", slug: "notes/current-system-map" as FullSlug },
  { label: "Code Algorithm Map", slug: "docs/code-algorithm-map" as FullSlug },
  {
    label: "Perfect Machine Domain Index",
    slug: "docs/perfect-machine-domain-index" as FullSlug,
  },
  {
    label: "Study Reading Guide",
    slug: "research/perfect-machine-study-2026-05-23/reading-guide" as FullSlug,
  },
  {
    label: "Calibration Session Spec",
    slug: "research/perfect-machine-study-2026-05-23/calibration-session-spec" as FullSlug,
  },
  {
    label: "Fensalir Integration Map",
    slug: "research/perfect-machine-study-2026-05-23/fensalir-integration-map" as FullSlug,
  },
];

export default (() => {
  const MimirOverviewSidebar: QuartzComponent = ({
    fileData,
  }: QuartzComponentProps) => {
    const currentSlug = fileData.slug ?? ("Mimir-Vault" as FullSlug);

    return (
      <aside class="mimir-overview" aria-label="Mimir machine surface">
        <p class="mimir-overview-title">Machine Surface</p>
        <p class="mimir-overview-copy">
          Start with the live map, then descend into the decoder, calibration,
          native ingest, and Fensalir cuts.
        </p>
        <nav>
          {links.map((link) => (
            <a href={resolveRelative(currentSlug, link.slug)}>{link.label}</a>
          ))}
        </nav>
      </aside>
    );
  };

  return MimirOverviewSidebar;
}) satisfies QuartzComponentConstructor;
