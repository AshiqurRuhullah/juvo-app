import { ScopedIntl } from "@/components/providers/scoped-intl";

/** The development gallery shows every component, so it gets every text. */
export default function DesignLayout({ children }: { children: React.ReactNode }) {
  return <ScopedIntl scope="admin">{children}</ScopedIntl>;
}
