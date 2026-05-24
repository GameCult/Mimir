import {
  QuartzComponent,
  QuartzComponentConstructor,
  QuartzComponentProps,
} from "./types";
import { FullSlug, resolveRelative } from "../util/path";

type Route = {
  label: string;
  slug: FullSlug;
  matches: string[];
};

const routes: Route[] = [
  {
    label: "Home",
    slug: "Mimir-Vault" as FullSlug,
    matches: ["Mimir-Vault", "index"],
  },
  {
    label: "Docs",
    slug: "docs/perfect-machine-domain-index" as FullSlug,
    matches: ["docs"],
  },
  {
    label: "Runtime",
    slug: "notes/current-system-map" as FullSlug,
    matches: ["notes", "state"],
  },
  {
    label: "Research",
    slug: "research/perfect-machine-study-2026-05-23/reading-guide" as FullSlug,
    matches: ["research"],
  },
  { label: "Native", slug: "native/README" as FullSlug, matches: ["native"] },
];

function isMatch(currentSlug: string, prefix: string) {
  return currentSlug === prefix || currentSlug.startsWith(`${prefix}/`);
}

function pickActiveRoute(currentSlug: string) {
  return routes
    .flatMap((route) =>
      route.matches
        .filter((prefix) => isMatch(currentSlug, prefix))
        .map((prefix) => ({ route, prefixLength: prefix.length })),
    )
    .sort((a, b) => b.prefixLength - a.prefixLength)[0]?.route;
}

export default (() => {
  const MimirMasthead: QuartzComponent = ({
    fileData,
  }: QuartzComponentProps) => {
    const currentSlug = fileData.slug ?? ("Mimir-Vault" as FullSlug);
    const activeRoute = pickActiveRoute(currentSlug);

    return (
      <section class="mimir-titlebar">
        <a
          class="mimir-titlebar-logo"
          href={resolveRelative(currentSlug, "Mimir-Vault" as FullSlug)}
          aria-label="Mimir home"
        >
          <img src="/Mimir.png" alt="" />
        </a>
        <div class="mimir-titlebar-copy">
          <p class="mimir-titlebar-title">
            <a href={resolveRelative(currentSlug, "Mimir-Vault" as FullSlug)}>
              Mimir
            </a>
          </p>
          <p class="mimir-titlebar-tagline">
            Realtime cameras, microphones, chirplets, and field evidence
            organized into one coherent stream machine.
          </p>
        </div>
        <nav class="mimir-titlebar-nav" aria-label="Mimir sections">
          {routes.map((route) => {
            const active = activeRoute?.slug === route.slug;
            return (
              <a
                href={resolveRelative(currentSlug, route.slug)}
                class={active ? "mimir-nav-chip active" : "mimir-nav-chip"}
              >
                {route.label}
              </a>
            );
          })}
        </nav>
      </section>
    );
  };

  return MimirMasthead;
}) satisfies QuartzComponentConstructor;
