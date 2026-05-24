import { QuartzComponent, QuartzComponentConstructor } from "./types";

export default (() => {
  const MimirThemeLock: QuartzComponent = () => (
    <script
      dangerouslySetInnerHTML={{
        __html: `document.documentElement.setAttribute("saved-theme", "dark")`,
      }}
    />
  );

  return MimirThemeLock;
}) satisfies QuartzComponentConstructor;
