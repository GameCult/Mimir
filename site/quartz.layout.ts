import { PageLayout, SharedLayout } from "./quartz/cfg";
import * as Component from "./quartz/components";
import MimirMasthead from "./quartz/components/MimirMasthead";
import MimirOverviewSidebar from "./quartz/components/MimirOverviewSidebar";
import MimirThemeLock from "./quartz/components/MimirThemeLock";

const MimirGraphShell = Component.GameCultGraphSpaShell({
  stylesheetHref:
    "/static/epiphany-graph/assets/viewer.css?v=graph-mimir-corpus-20260524",
  moduleSrc: "/static/epiphany-graph/assets/viewer.js?v=graph-mimir-corpus-20260524",
  config: {
    title: "Mimir Knowledge Graph",
    architectureDescription:
      "Notes are Mimir-owned docs, research, and implementation maps. Their edges are Quartz wiki links, with incoming backlinks counted into each node.",
    allowedSlugPrefixes: [
      "README",
      "index",
      "Mimir-Vault",
      "docs",
      "notes",
      "research",
      "native",
    ],
    blockedSlugPrefixes: [
      "GameCult-Quartz",
      "quartz-site",
      "site",
      "scripts",
      "src",
      "state",
      "tools",
    ],
    blockedPathSegments: [
      "/GameCult-Quartz/",
      "/node_modules/",
      "/quartz-site/",
      "/site/",
      "/scripts/",
      "/src/",
      "/state/",
      "/tools/",
    ],
  },
});

export const sharedPageComponents: SharedLayout = {
  head: Component.Head(),
  header: [MimirThemeLock(), MimirMasthead(), Component.Search()],
  afterBody: [],
  footer: Component.Footer({
    links: {},
  }),
};

export const defaultContentPageLayout: PageLayout = {
  beforeBody: [
    Component.ConditionalRender({
      component: Component.Breadcrumbs({
        rootName: "Mimir",
        showCurrentPage: false,
        showRoot: false,
      }),
      condition: (page) =>
        page.fileData.slug !== "Mimir-Vault" && page.fileData.slug !== "index",
    }),
    Component.ConditionalRender({
      component: Component.ArticleTitle(),
      condition: (page) =>
        !page.fileData.slug?.endsWith("/index") &&
        page.fileData.slug !== "Mimir-Vault",
    }),
    Component.ConditionalRender({
      component: Component.ContentMeta(),
      condition: (page) =>
        !page.fileData.slug?.endsWith("/index") &&
        page.fileData.slug !== "Mimir-Vault",
    }),
  ],
  afterBody: [
    Component.ConditionalRender({
      component: MimirGraphShell,
      condition: (page) =>
        page.fileData.slug === "Mimir-Vault" || page.fileData.slug === "index",
    }),
  ],
  left: [MimirOverviewSidebar()],
  right: [
    Component.DesktopOnly(Component.TableOfContents()),
    Component.Backlinks(),
  ],
};

export const defaultListPageLayout: PageLayout = {
  beforeBody: [
    Component.Breadcrumbs({
      rootName: "Mimir",
      showCurrentPage: false,
      showRoot: false,
    }),
  ],
  left: [],
  right: [],
};
