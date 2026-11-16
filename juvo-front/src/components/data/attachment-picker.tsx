"use client";

import * as React from "react";
import { FileText, Paperclip, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { mediaUrl } from "@/lib/media";

export type UploadedFile = { path?: string; fileName: string; url: string; size?: number | string; contentType?: string };

export type AttachmentLabels = {
  add: string;
  uploading: string;
  remove: (name: string) => string;
  hint: string;
  typeError: string;
  sizeError: string;
  open: (name: string) => string;
};

/**
 * Picks pictures or PDFs, checks them on the device (the API checks again), uploads each one and lists what is attached. Files that
 * are already saved (`saved`) are shown as links and cannot be removed here; new uploads (`added`) can be removed before saving.
 */
export function AttachmentPicker({
  saved = [],
  added,
  onAdded,
  upload,
  check,
  labels,
  onError,
  disabled,
  idBase,
}: {
  saved?: readonly UploadedFile[];
  added: readonly UploadedFile[];
  onAdded: (files: UploadedFile[]) => void;
  upload: (file: File) => Promise<UploadedFile>;
  check: (file: File) => "type" | "size" | null;
  labels: AttachmentLabels;
  onError: (error: unknown) => void;
  disabled?: boolean;
  idBase: string;
}) {
  const input = React.useRef<HTMLInputElement>(null);
  const [busy, setBusy] = React.useState(false);
  const [problem, setProblem] = React.useState<string | null>(null);

  const pick = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(event.target.files ?? []);
    event.target.value = "";
    setProblem(null);
    if (files.length === 0) return;
    const bad = files.map(check).find((p) => p !== null);
    if (bad) {
      setProblem(bad === "type" ? labels.typeError : labels.sizeError);
      return;
    }
    setBusy(true);
    try {
      const done: UploadedFile[] = [];
      for (const file of files) done.push(await upload(file));
      onAdded([...added, ...done]);
    } catch (error) {
      onError(error);
    } finally {
      setBusy(false);
    }
  };

  const all = [...saved.map((f) => ({ f, removable: false })), ...added.map((f) => ({ f, removable: true }))];
  return (
    <div className="space-y-2">
      <input ref={input} id={`${idBase}-file`} type="file" multiple accept="image/jpeg,image/png,image/webp,application/pdf" className="sr-only" tabIndex={-1} aria-hidden onChange={(e) => void pick(e)} />
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="secondary" loading={busy} disabled={disabled} onClick={() => input.current?.click()} data-testid={`${idBase}-add`}>
          <Paperclip aria-hidden /> {busy ? labels.uploading : labels.add}
        </Button>
        <p className="text-sm text-muted">{labels.hint}</p>
      </div>
      {problem ? (
        <p role="alert" className="text-sm font-medium text-danger">
          {problem}
        </p>
      ) : null}
      {all.length > 0 ? (
        <ul className="divide-y divide-line rounded-control border border-line" data-testid={`${idBase}-list`}>
          {all.map(({ f, removable }) => (
            <li key={f.url} className="flex items-center justify-between gap-2 px-3 py-1">
              <a href={mediaUrl(f.url)} target="_blank" rel="noopener noreferrer" className="inline-flex min-h-11 min-w-0 items-center gap-2 font-medium text-primary hover:underline" aria-label={labels.open(f.fileName)}>
                <FileText className="size-4 shrink-0" aria-hidden />
                <span className="[overflow-wrap:anywhere]">{f.fileName}</span>
              </a>
              {removable ? (
                <Button variant="ghost" size="icon" aria-label={labels.remove(f.fileName)} disabled={disabled} onClick={() => onAdded(added.filter((x) => x.url !== f.url))}>
                  <X aria-hidden />
                </Button>
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
