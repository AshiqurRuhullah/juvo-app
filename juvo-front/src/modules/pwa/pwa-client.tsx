"use client";

import * as React from "react";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { Download, Share, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { toast } from "@/components/ui/toaster";
import {
  countVisit,
  dismissInstall,
  EMPTY_STATE,
  iosHintSeen,
  isIosSafari,
  isQuietPath,
  markIosHintSeen,
  shouldShowInstall,
  type InstallState,
} from "@/lib/pwa/rules";

/** The event Chrome and Android browsers fire when the site can be installed (not in the standard lib types yet). */
interface BeforeInstallPromptEvent extends Event {
  prompt: () => Promise<void>;
  userChoice: Promise<{ outcome: "accepted" | "dismissed" }>;
}

function isStandalone(): boolean {
  try {
    return window.matchMedia("(display-mode: standalone)").matches || (navigator as Navigator & { standalone?: boolean }).standalone === true;
  } catch {
    return false;
  }
}

/**
 * Shop only (mounted by the storefront layout, never by the admin or account areas): registers the service worker in production
 * builds, offers the "new version" reload, and shows the respectful install prompt. See docs/pwa-notes.md.
 */
export function PwaClient() {
  const t = useTranslations("pwa");
  const pathname = usePathname();
  const [state, setState] = React.useState<InstallState>(EMPTY_STATE);
  const [deferred, setDeferred] = React.useState<BeforeInstallPromptEvent | null>(null);
  const [installed, setInstalled] = React.useState(true); // assume installed until measured, so nothing flashes on the server render
  const [iosSeen, setIosSeen] = React.useState(true);
  const [ios, setIos] = React.useState(false);
  const [now, setNow] = React.useState(0);
  const updateText = t("update.ready");
  const reloadText = t("update.reload");

  // Register the worker and watch for a waiting version. Production only: the dev server must never be cached.
  React.useEffect(() => {
    if (process.env.NODE_ENV !== "production" || !("serviceWorker" in navigator)) return;
    let cancelled = false;
    let reloading = false;
    const askToReload = (worker: ServiceWorker) => {
      toast(updateText, {
        id: "pwa-update",
        duration: Infinity,
        action: { label: reloadText, onClick: () => worker.postMessage({ type: "SKIP_WAITING" }) },
      });
    };
    const onControllerChange = () => {
      if (reloading) return;
      reloading = true;
      window.location.reload();
    };
    navigator.serviceWorker.addEventListener("controllerchange", onControllerChange);
    navigator.serviceWorker
      .register("/sw.js", { scope: "/" })
      .then((registration) => {
        if (cancelled) return;
        if (registration.waiting && navigator.serviceWorker.controller) askToReload(registration.waiting);
        registration.addEventListener("updatefound", () => {
          const worker = registration.installing;
          worker?.addEventListener("statechange", () => {
            if (worker.state === "installed" && navigator.serviceWorker.controller) askToReload(worker);
          });
        });
      })
      .catch(() => {
        /* no worker: the shop works as a normal site */
      });
    return () => {
      cancelled = true;
      navigator.serviceWorker.removeEventListener("controllerchange", onControllerChange);
    };
  }, [updateText, reloadText]);

  // Count this visit, learn whether the app is already installed, listen for the install event.
  React.useEffect(() => {
    if (process.env.NODE_ENV !== "production") return;
    /* eslint-disable react-hooks/set-state-in-effect -- reads browser-only storage once after mount */
    setNow(Date.now());
    setState(countVisit());
    setInstalled(isStandalone());
    setIosSeen(iosHintSeen());
    setIos(isIosSafari(navigator.userAgent));
    /* eslint-enable react-hooks/set-state-in-effect */
    const onPrompt = (e: Event) => {
      e.preventDefault();
      setDeferred(e as BeforeInstallPromptEvent);
    };
    const onInstalled = () => {
      setInstalled(true);
      setDeferred(null);
    };
    window.addEventListener("beforeinstallprompt", onPrompt);
    window.addEventListener("appinstalled", onInstalled);
    return () => {
      window.removeEventListener("beforeinstallprompt", onPrompt);
      window.removeEventListener("appinstalled", onInstalled);
    };
  }, []);

  if (isQuietPath(pathname)) return null;
  const eligible = shouldShowInstall(state, now, installed);
  const showInstall = eligible && deferred !== null;
  const showIos = eligible && ios && !iosSeen && deferred === null;
  if (!showInstall && !showIos) return null;

  const close = () => {
    if (showIos) {
      markIosHintSeen();
      setIosSeen(true);
    } else {
      setState(dismissInstall(Date.now()));
    }
  };
  const install = async () => {
    if (!deferred) return;
    await deferred.prompt();
    const choice = await deferred.userChoice.catch(() => null);
    setDeferred(null);
    if (choice?.outcome !== "accepted") setState(dismissInstall(Date.now()));
  };

  return (
    <section
      aria-label={t("install.region")}
      data-testid="pwa-prompt"
      className="safe-bottom fixed inset-x-3 bottom-3 z-40 mx-auto flex max-w-md items-start gap-3 rounded-card border border-line bg-card p-4 shadow-card sm:start-auto sm:end-4 sm:mx-0"
    >
      <span className="mt-0.5 text-primary" aria-hidden="true">
        {showIos ? <Share className="size-6" /> : <Download className="size-6" />}
      </span>
      <div className="min-w-0 flex-1 space-y-3">
        <div>
          <p className="font-semibold text-strong">{showIos ? t("ios.title") : t("install.title")}</p>
          <p className="text-sm text-muted">{showIos ? t("ios.body") : t("install.body")}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          {showInstall ? <Button onClick={install}>{t("install.action")}</Button> : null}
          <Button variant="secondary" onClick={close}>
            {showIos ? t("ios.dismiss") : t("install.dismiss")}
          </Button>
        </div>
      </div>
      <button
        type="button"
        onClick={close}
        aria-label={t("install.dismissLabel")}
        className="-me-2 -mt-2 inline-flex size-11 shrink-0 items-center justify-center rounded-control text-muted hover:bg-sunken hover:text-strong"
      >
        <X className="size-5" aria-hidden="true" />
      </button>
    </section>
  );
}
