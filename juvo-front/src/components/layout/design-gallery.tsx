"use client";

import * as React from "react";
import { Check, Plus, Search, Trash2 } from "lucide-react";
import type { ColumnDef } from "@tanstack/react-table";
import { Button } from "@/components/ui/button";
import { Checkbox, ChoiceRow, Field, Input, PasswordInput, RadioGroup, RadioItem, Switch, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle, EmptyState, ImageFrame, Skeleton } from "@/components/ui/surfaces";
import { Dialog, DialogClose, DialogDescription, DialogTitle, DialogTrigger, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger, ModalContent, SheetContent, Tooltip } from "@/components/ui/overlays";
import { Tabs } from "@/components/ui/tabs";
import { toast } from "@/components/ui/toaster";
import { DataTable } from "@/components/ui/data-table";

type Row = { id: string; name: string; district: string; orders: number; status: "Active" | "Blocked" };

const rows: Row[] = [
  { id: "1", name: "Rahim Uddin", district: "Dhaka", orders: 14, status: "Active" },
  { id: "2", name: "Nusrat Jahan Chowdhury (a very long customer name to test wrapping)", district: "Chattogram", orders: 3, status: "Active" },
  { id: "3", name: "ফারহানা ইয়াসমিন", district: "সিলেট", orders: 9, status: "Blocked" },
];

const districts = ["Dhaka", "Gazipur", "Narayanganj", "Chattogram", "Cox's Bazar", "Sylhet", "Rajshahi", "Khulna", "Barishal", "Rangpur", "Mymensingh"].map((d) => ({ value: d.toLowerCase(), label: d, hint: "Bangladesh" }));

/** The design system on one page (development and test only), so every component can be checked at every width. */
export function DesignGallery() {
  const [district, setDistrict] = React.useState<string | null>(null);
  const [tab, setTab] = React.useState("one");
  const [on, setOn] = React.useState(true);

  const columns = React.useMemo<ColumnDef<Row, unknown>[]>(
    () => [
      { accessorKey: "name", header: "Customer", meta: { primary: true } },
      { accessorKey: "district", header: "District" },
      { accessorKey: "orders", header: "Orders", meta: { align: "end" } },
      { id: "status", header: "Status", cell: ({ row }) => <Badge variant={row.original.status === "Active" ? "success" : "danger"}>{row.original.status}</Badge> },
      { id: "actions", header: "", meta: { actions: true, align: "end" }, cell: () => <Button variant="secondary" size="icon" aria-label="Delete row"><Trash2 aria-hidden /></Button> },
    ],
    [],
  );

  return (
    <div className="space-y-10" data-testid="design-gallery">
      <section className="space-y-3" aria-labelledby="g-buttons">
        <h2 id="g-buttons" className="text-xl font-semibold">Buttons</h2>
        <div className="flex flex-wrap gap-3">
          <Button>Primary</Button>
          <Button variant="secondary">Secondary</Button>
          <Button variant="ghost">Ghost</Button>
          <Button variant="danger">Danger</Button>
          <Button variant="link">Link style</Button>
          <Button loading>Saving</Button>
          <Button disabled>Disabled</Button>
          <Button size="lg"><Plus aria-hidden /> Large with icon</Button>
          <Button size="icon" aria-label="Search"><Search aria-hidden /></Button>
        </div>
      </section>

      <section className="space-y-3" aria-labelledby="g-badges">
        <h2 id="g-badges" className="text-xl font-semibold">Badges and alerts</h2>
        <div className="flex flex-wrap gap-2">
          <Badge>Neutral</Badge>
          <Badge variant="brand">New</Badge>
          <Badge variant="sale">Sale -20%</Badge>
          <Badge variant="warning">Low stock</Badge>
          <Badge variant="danger">Sold out</Badge>
          <Badge variant="success">Free delivery</Badge>
          <Badge variant="info">Pre-order</Badge>
          <Badge variant="outline">Soon</Badge>
        </div>
        <div className="grid gap-3 md:grid-cols-2">
          <Alert variant="info" title="Information">Orders placed before 3 pm ship the same day.</Alert>
          <Alert variant="success" title="Saved">The product was published.</Alert>
          <Alert variant="warning" title="Check this">Three variants have no stock.</Alert>
          <Alert variant="danger" title="Could not save">The SKU is already used by another product.</Alert>
        </div>
      </section>

      <section className="space-y-4" aria-labelledby="g-forms">
        <h2 id="g-forms" className="text-xl font-semibold">Form controls</h2>
        <div className="grid gap-4 md:grid-cols-2">
          <Field label="Full name" htmlFor="g-name" hint="As it should appear on the parcel." required><Input id="g-name" placeholder="Rahim Uddin" /></Field>
          <Field label="Mobile number" htmlFor="g-mobile" error="Enter a valid mobile number."><Input id="g-mobile" inputMode="tel" defaultValue="0171" aria-invalid /></Field>
          <Field label="Password" htmlFor="g-pw"><PasswordInput id="g-pw" defaultValue="secret" /></Field>
          <Field label="District" htmlFor="g-district" required>
            <Select id="g-district" value={district} onValueChange={setDistrict} options={districts} placeholder="Choose a district" searchPlaceholder="Search districts" emptyText="No district found" />
          </Field>
          <Field label="Delivery note" htmlFor="g-note" className="md:col-span-2"><Textarea id="g-note" placeholder="Gift wrap, call before arriving..." /></Field>
        </div>
        <div className="grid gap-2 md:grid-cols-3">
          <ChoiceRow htmlFor="g-check"><Checkbox id="g-check" defaultChecked /> Send me offers</ChoiceRow>
          <RadioGroup defaultValue="cod" aria-label="Payment">
            <ChoiceRow htmlFor="g-cod"><RadioItem id="g-cod" value="cod" /> Cash on delivery</ChoiceRow>
            <ChoiceRow htmlFor="g-online"><RadioItem id="g-online" value="online" /> Pay online</ChoiceRow>
          </RadioGroup>
          <ChoiceRow htmlFor="g-switch"><Switch id="g-switch" checked={on} onCheckedChange={setOn} /> Show out-of-stock items</ChoiceRow>
        </div>
      </section>

      <section className="space-y-3" aria-labelledby="g-cards">
        <h2 id="g-cards" className="text-xl font-semibold">Cards, image frame, skeleton, empty state</h2>
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <Card interactive>
            <ImageFrame src="/brand/logo-original.webp" alt="Sample 4:5 product frame" width={512} height={640} className="rounded-b-none" />
            <CardHeader><CardTitle className="text-lg">Silk saree</CardTitle><CardDescription>৳ 4,500 <s className="text-muted">৳ 5,200</s></CardDescription></CardHeader>
            <CardFooter className="border-0 pt-0"><Badge variant="sale">-13%</Badge><Badge variant="warning">Low stock</Badge></CardFooter>
          </Card>
          <Card><CardHeader><CardTitle>Loading</CardTitle></CardHeader><CardContent className="space-y-2"><Skeleton className="h-5 w-3/4" /><Skeleton className="h-5 w-full" /><Skeleton className="h-24 w-full" /></CardContent></Card>
          <Card className="sm:col-span-2"><EmptyState icon={<Check aria-hidden />} title="Nothing to review" description="New fraud flags and pending payments will be listed here." action={<Button variant="secondary">Refresh</Button>} /></Card>
        </div>
      </section>

      <section className="space-y-3" aria-labelledby="g-overlays">
        <h2 id="g-overlays" className="text-xl font-semibold">Overlays, menus, tabs, toasts</h2>
        <div className="flex flex-wrap items-center gap-3">
          <Dialog>
            <DialogTrigger asChild><Button variant="secondary">Open modal</Button></DialogTrigger>
            <ModalContent closeLabel="Close" size="sm" aria-describedby="g-modal-desc">
              <DialogTitle className="font-display text-xl font-semibold">Archive this product?</DialogTitle>
              <DialogDescription id="g-modal-desc" className="mt-2 text-muted">Archived products disappear from the shop but stay in old orders.</DialogDescription>
              <div className="mt-6 flex flex-wrap justify-end gap-3"><DialogClose asChild><Button variant="secondary">Cancel</Button></DialogClose><DialogClose asChild><Button variant="danger">Archive</Button></DialogClose></div>
            </ModalContent>
          </Dialog>
          <Dialog>
            <DialogTrigger asChild><Button variant="secondary">Open bottom sheet</Button></DialogTrigger>
            <SheetContent side="bottom" closeLabel="Close" aria-describedby="g-sheet-desc">
              <div className="p-5 pt-12"><DialogTitle className="font-display text-xl font-semibold">Filters</DialogTitle><DialogDescription id="g-sheet-desc" className="mt-1 text-muted">Narrow the list.</DialogDescription></div>
            </SheetContent>
          </Dialog>
          <DropdownMenu>
            <DropdownMenuTrigger asChild><Button variant="secondary">Row actions</Button></DropdownMenuTrigger>
            <DropdownMenuContent><DropdownMenuItem>Edit</DropdownMenuItem><DropdownMenuItem>Duplicate</DropdownMenuItem><DropdownMenuItem>Archive</DropdownMenuItem></DropdownMenuContent>
          </DropdownMenu>
          <Tooltip content="Tooltips show on hover and focus"><Button variant="ghost">Hover me</Button></Tooltip>
          <Button variant="secondary" onClick={() => toast.success("Product published")}>Success toast</Button>
          <Button variant="secondary" onClick={() => toast.error("Could not save the product")}>Error toast</Button>
        </div>
        <Tabs label="Example tabs" value={tab} onValueChange={setTab} tabs={[{ id: "one", label: "Details" }, { id: "two", label: "Variants" }]}>
          {(active) => <p className="text-muted">Selected tab: {active}</p>}
        </Tabs>
      </section>

      <section className="space-y-3" aria-labelledby="g-table">
        <h2 id="g-table" className="text-xl font-semibold">Data table (cards on phones)</h2>
        <DataTable
          caption="Customers"
          columns={columns}
          data={rows}
          getRowId={(r) => r.id}
          page={1}
          pageSize={3}
          total={27}
          onPageChange={() => undefined}
          labels={{ empty: "No customers yet", previous: "Previous page", next: "Next page", pageOf: (p, n) => `Page ${p} of ${n}`, total: (n) => `${n} customers`, sortBy: (c) => `Sort by ${c}` }}
        />
      </section>
    </div>
  );
}
