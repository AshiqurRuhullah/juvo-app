import type { ProductDetail, SaveProduct, TargetGroup } from "./api";
import { toNumber } from "./format";

/** What the product form edits. Everything is text (as typed) except the switches; conversion to the API's request happens in one place. */
export type ProductDraft = {
  nameEn: string;
  nameBn: string;
  slug: string;
  categoryId: string;
  brandId: string;
  shortDescription: string;
  description: string;
  material: string;
  care: string;
  tags: string;
  targetGroup: TargetGroup;
  weightGrams: string;
  lengthCm: string;
  widthCm: string;
  heightCm: string;
  regularPrice: string;
  salePrice: string;
  /** Day (yyyy-mm-dd) in Bangladesh time. */
  saleStart: string;
  saleEnd: string;
  costPrice: string;
  isNew: boolean;
  isFeatured: boolean;
  isBestSeller: boolean;
  sizeChartId: string;
  metaTitle: string;
  metaDescription: string;
  videoUrl: string;
};

export type DraftErrors = Partial<Record<keyof ProductDraft, string>>;

const DHAKA = "+06:00";

export function emptyDraft(): ProductDraft {
  return {
    nameEn: "", nameBn: "", slug: "", categoryId: "", brandId: "", shortDescription: "", description: "", material: "", care: "", tags: "", targetGroup: "Unisex",
    weightGrams: "", lengthCm: "", widthCm: "", heightCm: "", regularPrice: "", salePrice: "", saleStart: "", saleEnd: "", costPrice: "",
    isNew: false, isFeatured: false, isBestSeller: false, sizeChartId: "", metaTitle: "", metaDescription: "", videoUrl: "",
  };
}

const text = (value: number | string | null | undefined) => (value === null || value === undefined ? "" : String(value));

/** A moment as a day in Asia/Dhaka (what the sale dates mean in this business). */
function dhakaDay(iso: string | null | undefined): string {
  if (!iso) return "";
  return new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Dhaka", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date(iso));
}

export function draftFromProduct(p: ProductDetail): ProductDraft {
  return {
    nameEn: p.nameEn, nameBn: p.nameBn ?? "", slug: p.slug, categoryId: p.categoryId, brandId: p.brandId ?? "", shortDescription: p.shortDescription ?? "",
    description: p.description ?? "", material: p.material ?? "", care: p.care ?? "", tags: p.tags ?? "", targetGroup: p.targetGroup,
    weightGrams: text(p.weightGrams), lengthCm: text(p.lengthCm), widthCm: text(p.widthCm), heightCm: text(p.heightCm),
    regularPrice: text(p.regularPrice), salePrice: text(p.salePrice), saleStart: dhakaDay(p.saleStart), saleEnd: dhakaDay(p.saleEnd),
    costPrice: p.costHidden ? "" : text(p.costPrice), isNew: p.isNew, isFeatured: p.isFeatured, isBestSeller: p.isBestSeller, sizeChartId: p.sizeChartId ?? "",
    metaTitle: p.metaTitle ?? "", metaDescription: p.metaDescription ?? "", videoUrl: p.videoUrl ?? "",
  };
}

const blank = (value: string) => (value.trim() === "" ? null : value.trim());
const decimal = (value: string) => (value.trim() === "" ? null : Number(value.trim().replace(",", ".")));
const whole = (value: string) => (value.trim() === "" ? null : Math.trunc(Number(value.trim())));

/** The API request for a draft. `costHidden`: staff who cannot see the cost send nothing and the API keeps the stored one. */
export function draftToRequest(d: ProductDraft, existing?: Pick<ProductDetail, "socialImagePath">): SaveProduct {
  return {
    nameEn: d.nameEn.trim(),
    nameBn: blank(d.nameBn),
    slug: blank(d.slug),
    categoryId: d.categoryId,
    brandId: d.brandId || null,
    shortDescription: blank(d.shortDescription),
    description: blank(d.description),
    material: blank(d.material),
    care: blank(d.care),
    tags: blank(d.tags),
    targetGroup: d.targetGroup,
    weightGrams: whole(d.weightGrams),
    lengthCm: decimal(d.lengthCm),
    widthCm: decimal(d.widthCm),
    heightCm: decimal(d.heightCm),
    regularPrice: decimal(d.regularPrice) ?? 0,
    salePrice: decimal(d.salePrice),
    saleStart: d.saleStart ? `${d.saleStart}T00:00:00${DHAKA}` : null,
    saleEnd: d.saleEnd ? `${d.saleEnd}T23:59:59${DHAKA}` : null,
    costPrice: decimal(d.costPrice),
    isNew: d.isNew,
    isFeatured: d.isFeatured,
    isBestSeller: d.isBestSeller,
    sizeChartId: d.sizeChartId || null,
    metaTitle: blank(d.metaTitle),
    metaDescription: blank(d.metaDescription),
    socialImagePath: existing?.socialImagePath ?? null,
    videoUrl: blank(d.videoUrl),
  };
}

type Translate = (key: string) => string;

const isNumber = (value: string) => value.trim() === "" || (/^\d+([.,]\d{1,2})?$/.test(value.trim()) && Number.isFinite(Number(value.trim().replace(",", "."))));
const isWhole = (value: string) => value.trim() === "" || /^\d+$/.test(value.trim());

/** The same rules the API applies (BR-B2 and field lengths), so mistakes show next to the field before saving. */
export function validateDraft(d: ProductDraft, t: Translate, canSeeCost: boolean): DraftErrors {
  const errors: DraftErrors = {};
  if (!d.nameEn.trim()) errors.nameEn = t("nameRequired");
  else if (d.nameEn.trim().length > 200) errors.nameEn = t("nameTooLong");
  if (!d.categoryId) errors.categoryId = t("categoryRequired");
  if (d.slug.trim() && !/^[a-z0-9]+(-[a-z0-9]+)*$/.test(d.slug.trim())) errors.slug = t("slugInvalid");

  if (d.regularPrice.trim() === "") errors.regularPrice = t("priceRequired");
  else if (!isNumber(d.regularPrice)) errors.regularPrice = t("amountInvalid");

  if (d.salePrice.trim() !== "") {
    if (!isNumber(d.salePrice)) errors.salePrice = t("amountInvalid");
    else if (!errors.regularPrice && Number(d.salePrice.replace(",", ".")) >= Number(d.regularPrice.replace(",", "."))) errors.salePrice = t("saleBelowRegular");
  } else if (d.saleStart || d.saleEnd) {
    errors.salePrice = t("saleNeedsPrice");
  }
  if (d.saleStart && d.saleEnd && d.saleEnd < d.saleStart) errors.saleEnd = t("saleEndAfterStart");
  if (canSeeCost && !isNumber(d.costPrice)) errors.costPrice = t("amountInvalid");

  for (const key of ["lengthCm", "widthCm", "heightCm"] as const) if (!isNumber(d[key])) errors[key] = t("amountInvalid");
  if (!isWhole(d.weightGrams)) errors.weightGrams = t("wholeInvalid");
  if (d.metaTitle.length > 120) errors.metaTitle = t("metaTitleTooLong");
  if (d.metaDescription.length > 320) errors.metaDescription = t("metaDescriptionTooLong");
  if (d.videoUrl.trim() && !/^https?:\/\/\S+$/i.test(d.videoUrl.trim())) errors.videoUrl = t("urlInvalid");
  return errors;
}

/** The price a customer pays at `now`: the sale price inside its dates, otherwise the regular price (same rule as the API). */
export function effectivePrice(regularText: string, saleText: string, saleStart: string, saleEnd: string, now = new Date()): number {
  const regular = toNumber(regularText.replace(",", "."));
  const sale = saleText.trim() === "" ? null : toNumber(saleText.replace(",", "."));
  if (sale === null || sale >= regular) return regular;
  const started = !saleStart || now >= new Date(`${saleStart}T00:00:00${DHAKA}`);
  const notEnded = !saleEnd || now <= new Date(`${saleEnd}T23:59:59${DHAKA}`);
  return started && notEnded ? sale : regular;
}
