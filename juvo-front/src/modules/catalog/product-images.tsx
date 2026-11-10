"use client";

import * as React from "react";
import Image from "next/image";
import { useTranslations } from "next-intl";
import { ArrowLeft, ArrowRight, CheckCircle2, CircleAlert, Crosshair, ImagePlus, Loader2, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Input } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { Can } from "@/components/providers/session-provider";
import { cn } from "@/lib/utils";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { uploadProductImage, useDeleteImage, useReorderImages, useReportError, useUpdateImage, type ProductDetail, type ProductImage } from "./api";
import { cropWindow, imageSrc, toNumber } from "./format";
import { useQueryClient } from "@tanstack/react-query";

const MAX_BYTES = 10 * 1024 * 1024;
const MIN_LONG_SIDE = 1000;
const TYPES = ["image/jpeg", "image/png", "image/webp"];
const NO_COLOUR = "__none";

type UploadItem = { id: number; name: string; state: "waiting" | "uploading" | "done" | "error"; message?: string };

/** Natural size of a picture file, read in the browser, so a too-small picture is refused before it is sent. */
function measure(file: File): Promise<{ width: number; height: number } | null> {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const img = new window.Image();
    img.onload = () => {
      resolve({ width: img.naturalWidth, height: img.naturalHeight });
      URL.revokeObjectURL(url);
    };
    img.onerror = () => {
      resolve(null);
      URL.revokeObjectURL(url);
    };
    img.src = url;
  });
}

/** Edit one picture: where the 4:5 card crop is centred (click the picture or use the sliders), its colour and its alt text (FR-B04, FR-B05). */
function PictureBody({ image, colours, productId, onOpenChange }: { image: ProductImage; colours: string[]; productId: string; onOpenChange: (open: boolean) => void }) {
  const t = useTranslations("catalog.images");
  const tc = useTranslations("catalog.common");
  const report = useReportError();
  const update = useUpdateImage(productId);
  const [fx, setFx] = React.useState(() => toNumber(image.focalX));
  const [fy, setFy] = React.useState(() => toNumber(image.focalY));
  const [colour, setColour] = React.useState(image.colorName ?? "");
  const [alt, setAlt] = React.useState(image.altText ?? "");
  const frame = React.useRef<HTMLDivElement>(null);
  const width = toNumber(image.width);
  const height = toNumber(image.height);
  const crop = cropWindow(width, height, fx, fy);
  const clamp = (v: number) => Math.min(1, Math.max(0, v));

  const point = (event: React.PointerEvent) => {
    const box = frame.current?.getBoundingClientRect();
    if (!box) return;
    setFx(clamp((event.clientX - box.left) / box.width));
    setFy(clamp((event.clientY - box.top) / box.height));
  };

  const save = async () => {
    try {
      await update.mutateAsync({ imageId: image.id, body: { colorName: colour.trim() || null, altText: alt.trim() || null, focalX: Number(fx.toFixed(3)), focalY: Number(fy.toFixed(3)) } });
      toast.success(t("saved"));
      onOpenChange(false);
    } catch (error) {
      report(error);
    }
  };

  const colourOptions = [{ value: NO_COLOUR, label: t("noColour") }, ...colours.map((c) => ({ value: c, label: c }))];

  return (
    <>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{t("editTitle")}</DialogTitle>
        <DialogDescription className="mt-1 text-muted">{t("focalHint")}</DialogDescription>
        <div className="mt-4 grid gap-5 md:grid-cols-[minmax(0,1fr)_14rem]">
          <div>
            <div
              ref={frame}
              className="relative touch-none select-none overflow-hidden rounded-card border border-line bg-sunken"
              style={{ aspectRatio: `${width} / ${height}` }}
              onPointerDown={(e) => {
                e.currentTarget.setPointerCapture(e.pointerId);
                point(e);
              }}
              onPointerMove={(e) => e.buttons === 1 && point(e)}
              data-testid="focal-frame"
            >
              <Image src={imageSrc(image, "main")} alt={image.altText ?? ""} fill unoptimized sizes="(min-width: 768px) 32rem, 90vw" className="pointer-events-none object-cover" />
              <div
                className="pointer-events-none absolute rounded-sm border-2 border-white shadow-[0_0_0_9999px_rgba(0,0,0,0.45)]"
                style={{ left: `${crop.left * 100}%`, top: `${crop.top * 100}%`, width: `${crop.width * 100}%`, height: `${crop.height * 100}%` }}
                aria-hidden
              />
              <Crosshair className="pointer-events-none absolute size-6 -translate-x-1/2 -translate-y-1/2 text-white drop-shadow" style={{ left: `${fx * 100}%`, top: `${fy * 100}%` }} aria-hidden />
            </div>
            <div className="mt-3 grid gap-3 sm:grid-cols-2">
              <Field label={t("focalX")} htmlFor="focal-x">
                <input id="focal-x" type="range" min={0} max={100} value={Math.round(fx * 100)} onChange={(e) => setFx(Number(e.target.value) / 100)} className="h-11 w-full accent-[var(--brand-primary)]" />
              </Field>
              <Field label={t("focalY")} htmlFor="focal-y">
                <input id="focal-y" type="range" min={0} max={100} value={Math.round(fy * 100)} onChange={(e) => setFy(Number(e.target.value) / 100)} className="h-11 w-full accent-[var(--brand-primary)]" />
              </Field>
            </div>
          </div>
          <div className="space-y-4">
            <div>
              <p className="mb-1.5 text-sm font-semibold text-strong">{t("cardPreview")}</p>
              <div className="relative aspect-[4/5] w-40 overflow-hidden rounded-card border border-line bg-sunken md:w-full" aria-hidden>
                <Image src={imageSrc(image, "main")} alt="" fill unoptimized sizes="14rem" className="max-w-none object-cover" style={{ objectPosition: `${fx * 100}% ${fy * 100}%` }} />
              </div>
            </div>
            <Field label={t("colour")} htmlFor="img-colour" hint={t("colourHint")}>
              <Select
                id="img-colour"
                value={colour || NO_COLOUR}
                onValueChange={(v) => setColour(v === NO_COLOUR ? "" : v)}
                options={colourOptions}
                placeholder={t("colour")}
                searchPlaceholder={tc("search")}
                emptyText={tc("noMatch")}
              />
            </Field>
            <Field label={t("alt")} htmlFor="img-alt" hint={t("altHint")}>
              <Input id="img-alt" value={alt} onChange={(e) => setAlt(e.target.value)} maxLength={200} autoComplete="off" />
            </Field>
          </div>
        </div>
        <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={update.isPending}>
            {tc("cancel")}
          </Button>
          <Button onClick={() => void save()} loading={update.isPending}>
            {tc("save")}
          </Button>
        </div>
    </>
  );
}

function PictureDialog({ image, colours, productId, open, onOpenChange }: { image: ProductImage | null; colours: string[]; productId: string; open: boolean; onOpenChange: (open: boolean) => void }) {
  const tc = useTranslations("catalog.common");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="lg" closeLabel={tc("close")}>
        {open && image ? <PictureBody image={image} colours={colours} productId={productId} onOpenChange={onOpenChange} /> : null}
      </ModalContent>
    </Dialog>
  );
}

/** Pictures of a product: upload (several at once), colour sets, drag or button reordering, focal point, delete. The first picture is the card picture. */
export function ProductImages({ product, disabled }: { product: ProductDetail; disabled?: boolean }) {
  const t = useTranslations("catalog.images");
  const tc = useTranslations("catalog.common");
  const message = useProblemMessage();
  const report = useReportError();
  const queryClient = useQueryClient();
  const reorder = useReorderImages(product.id);
  const remove = useDeleteImage(product.id);

  const sorted = React.useMemo(() => [...product.images].sort((a, b) => toNumber(a.sortOrder) - toNumber(b.sortOrder)), [product.images]);
  const [pendingOrder, setPendingOrder] = React.useState<string[] | null>(null);
  const order = pendingOrder ?? sorted.map((i) => i.id);
  const byId = React.useMemo(() => new Map(sorted.map((i) => [i.id, i])), [sorted]);
  const ordered = order.map((id) => byId.get(id)).filter((i): i is ProductImage => !!i);

  const colours = React.useMemo(() => [...new Set([...product.variants.map((v) => v.colorName), ...product.images.map((i) => i.colorName)].filter((c): c is string => !!c))], [product.variants, product.images]);

  const [uploadColour, setUploadColour] = React.useState("");
  const [queue, setQueue] = React.useState<UploadItem[]>([]);
  const [busy, setBusy] = React.useState(false);
  const [dragOver, setDragOver] = React.useState(false);
  const [editing, setEditing] = React.useState<ProductImage | null>(null);
  const [deleting, setDeleting] = React.useState<ProductImage | null>(null);
  const [dragId, setDragId] = React.useState<string | null>(null);
  const counter = React.useRef(0);

  const upload = async (files: File[]) => {
    if (files.length === 0 || busy) return;
    const items: UploadItem[] = files.map((file) => ({ id: counter.current++, name: file.name, state: "waiting" }));
    setQueue(items);
    setBusy(true);
    let done = 0;
    for (const [index, file] of files.entries()) {
      const item = items[index]!;
      const patch = (state: UploadItem["state"], messageText?: string) => setQueue((q) => q.map((x) => (x.id === item.id ? { ...x, state, message: messageText } : x)));
      patch("uploading");
      if (!TYPES.includes(file.type)) {
        patch("error", t("errorType"));
        continue;
      }
      if (file.size > MAX_BYTES) {
        patch("error", t("errorSize"));
        continue;
      }
      const size = await measure(file);
      if (!size) {
        patch("error", t("errorType"));
        continue;
      }
      if (Math.max(size.width, size.height) < MIN_LONG_SIDE) {
        patch("error", t("errorSmall", { width: size.width, height: size.height }));
        continue;
      }
      try {
        await uploadProductImage(product.id, { file, colorName: uploadColour || null, altText: product.nameEn });
        patch("done");
        done++;
      } catch (error) {
        patch("error", error instanceof ApiError ? (error.problem.errors?.file?.[0] ?? message(error.problem)) : message(null));
      }
    }
    setBusy(false);
    await queryClient.invalidateQueries({ queryKey: ["catalog"] });
    if (done > 0) toast.success(t("uploaded", { count: done }));
  };

  const move = async (from: number, to: number) => {
    if (to < 0 || to >= order.length || from === to) return;
    const next = [...order];
    const [item] = next.splice(from, 1);
    next.splice(to, 0, item!);
    setPendingOrder(next);
    try {
      await reorder.mutateAsync(next);
    } catch (error) {
      report(error);
    } finally {
      setPendingOrder(null); // the refreshed list is the truth again (also after a refused change)
    }
  };

  return (
    <div className="space-y-5">
      <Card>
        <CardHeader>
          <CardTitle>{t("title")}</CardTitle>
          <CardDescription>{t("rules")}</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {!disabled ? (
            <Can permission="catalog.media.upload" scope="anywhere">
              <div className="grid gap-4 md:grid-cols-[minmax(0,1fr)_16rem] md:items-end">
                <label
                  htmlFor="picture-files"
                  onDragOver={(e) => {
                    e.preventDefault();
                    setDragOver(true);
                  }}
                  onDragLeave={() => setDragOver(false)}
                  onDrop={(e) => {
                    e.preventDefault();
                    setDragOver(false);
                    void upload([...e.dataTransfer.files]);
                  }}
                  className={cn(
                    "flex min-h-32 cursor-pointer flex-col items-center justify-center gap-2 rounded-card border-2 border-dashed border-line bg-sunken/50 p-5 text-center transition-colors focus-within:border-primary focus-within:ring-4 focus-within:ring-[var(--brand-primary-glow)] [@media(hover:hover)]:hover:border-primary",
                    dragOver && "border-primary bg-primary-soft",
                    busy && "pointer-events-none opacity-70",
                  )}
                >
                  {busy ? <Loader2 className="size-7 animate-spin text-primary" aria-hidden /> : <ImagePlus className="size-7 text-primary" aria-hidden />}
                  <span className="font-semibold text-strong">{t("choose")}</span>
                  <span className="text-sm text-muted">{t("dropHint")}</span>
                  <input
                    id="picture-files"
                    type="file"
                    multiple
                    accept="image/jpeg,image/png,image/webp"
                    className="sr-only"
                    disabled={busy}
                    onChange={(e) => {
                      void upload([...(e.target.files ?? [])]);
                      e.target.value = "";
                    }}
                  />
                </label>
                <Field label={t("uploadColour")} htmlFor="upload-colour" hint={t("uploadColourHint")}>
                  <Select
                    id="upload-colour"
                    value={uploadColour || NO_COLOUR}
                    onValueChange={(v) => setUploadColour(v === NO_COLOUR ? "" : v)}
                    options={[{ value: NO_COLOUR, label: t("noColour") }, ...colours.map((c) => ({ value: c, label: c }))]}
                    placeholder={t("uploadColour")}
                    searchPlaceholder={tc("search")}
                    emptyText={tc("noMatch")}
                  />
                </Field>
              </div>
            </Can>
          ) : null}

          {queue.length > 0 ? (
            <ul className="space-y-1.5" aria-live="polite" aria-label={t("queue")}>
              {queue.map((item) => (
                <li key={item.id} className="flex items-start gap-2 text-sm">
                  {item.state === "done" ? <CheckCircle2 className="mt-0.5 size-5 shrink-0 text-success" aria-hidden /> : item.state === "error" ? <CircleAlert className="mt-0.5 size-5 shrink-0 text-danger" aria-hidden /> : <Loader2 className={cn("mt-0.5 size-5 shrink-0 text-muted", item.state === "uploading" && "animate-spin")} aria-hidden />}
                  <span className="min-w-0 [overflow-wrap:anywhere]">
                    <span className="font-semibold text-strong">{item.name}</span>{" "}
                    <span className={item.state === "error" ? "text-danger" : "text-muted"}>{item.state === "error" ? item.message : t(`state.${item.state}`)}</span>
                  </span>
                </li>
              ))}
            </ul>
          ) : null}

          {ordered.length === 0 ? <Alert variant="info">{t("none")}</Alert> : null}
          {ordered.length > 0 ? (
            <ul className="grid grid-cols-[repeat(auto-fill,minmax(12.5rem,1fr))] gap-3" aria-label={t("gallery")}>
              {ordered.map((image, index) => (
                <li
                  key={image.id}
                  draggable={!disabled}
                  onDragStart={() => setDragId(image.id)}
                  onDragOver={(e) => dragId && e.preventDefault()}
                  onDrop={(e) => {
                    e.preventDefault();
                    if (dragId && dragId !== image.id) void move(order.indexOf(dragId), index);
                    setDragId(null);
                  }}
                  onDragEnd={() => setDragId(null)}
                  className={cn("overflow-hidden rounded-card border border-line bg-card shadow-card", dragId === image.id && "opacity-50")}
                  data-testid="picture"
                >
                  <div className="relative aspect-[4/5] bg-sunken">
                    <Image src={imageSrc(image, "card")} alt={image.altText ?? product.nameEn} fill unoptimized sizes="(min-width: 1024px) 16rem, 45vw" placeholder={image.blurDataUrl ? "blur" : "empty"} blurDataURL={image.blurDataUrl ?? undefined} className="object-cover" />
                    <div className="absolute start-2 top-2 flex flex-col items-start gap-1">
                      {index === 0 ? <Badge variant="sale">{t("primary")}</Badge> : null}
                      {image.colorName ? <Badge variant="neutral" className="bg-card/95">{image.colorName}</Badge> : null}
                    </div>
                  </div>
                  {!disabled ? (
                    <div className="flex items-center justify-between gap-1 p-2">
                      <div className="flex">
                        <Button variant="ghost" size="icon" aria-label={t("moveEarlier", { position: index + 1 })} disabled={index === 0 || reorder.isPending} onClick={() => void move(index, index - 1)}>
                          <ArrowLeft className="rtl:rotate-180" aria-hidden />
                        </Button>
                        <Button variant="ghost" size="icon" aria-label={t("moveLater", { position: index + 1 })} disabled={index === ordered.length - 1 || reorder.isPending} onClick={() => void move(index, index + 1)}>
                          <ArrowRight className="rtl:rotate-180" aria-hidden />
                        </Button>
                      </div>
                      <div className="flex">
                        <Can permission="catalog.media.upload" scope="anywhere">
                          <Button variant="ghost" size="icon" aria-label={t("edit", { position: index + 1 })} onClick={() => setEditing(image)}>
                            <Crosshair aria-hidden />
                          </Button>
                        </Can>
                        <Can permission="catalog.media.delete" scope="anywhere">
                          <Button variant="ghost" size="icon" aria-label={t("delete", { position: index + 1 })} onClick={() => setDeleting(image)}>
                            <Trash2 aria-hidden />
                          </Button>
                        </Can>
                      </div>
                    </div>
                  ) : null}
                </li>
              ))}
            </ul>
          ) : null}
        </CardContent>
      </Card>

      <PictureDialog image={editing} colours={colours} productId={product.id} open={!!editing} onOpenChange={(open) => !open && setEditing(null)} />
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t("deleteTitle")}
        description={t("deleteDescription")}
        confirmLabel={t("deleteConfirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={remove.isPending}
        onConfirm={async () => {
          if (!deleting) return;
          try {
            await remove.mutateAsync(deleting.id);
            toast.success(t("deleted"));
          } catch (error) {
            report(error);
          }
          setDeleting(null);
        }}
      />
    </div>
  );
}
