"use client";

import * as React from "react";
import L from "leaflet";
import "leaflet/dist/leaflet.css";

/** Dhaka. Used as the starting view when a branch has no pin yet. */
const DEFAULT_CENTER: [number, number] = [23.8103, 90.4125];

/** The pin is an inline SVG in the brand colour, so no image files from the Leaflet package are needed. */
const pinIcon = L.divIcon({
  className: "juvo-pin",
  html: '<svg width="36" height="46" viewBox="0 0 36 46" aria-hidden="true"><path d="M18 1C9 1 2 8 2 17c0 12 16 28 16 28s16-16 16-28C34 8 27 1 18 1z" fill="#DB0100" stroke="#fff" stroke-width="2"/><circle cx="18" cy="17" r="6" fill="#fff"/></svg>',
  iconSize: [36, 46],
  iconAnchor: [18, 45],
});

/**
 * Pick a branch location: click the map or drag the pin. The latitude and longitude fields beside it stay the accessible way to
 * enter a position. Map tiles come from OpenStreetMap (fine for the admin panel; a tile provider with a key is chosen in Step 14).
 */
export default function MapPicker({ latitude, longitude, onChange, label }: { latitude: number | null; longitude: number | null; onChange: (lat: number, lng: number) => void; label: string }) {
  const container = React.useRef<HTMLDivElement>(null);
  const mapRef = React.useRef<L.Map | null>(null);
  const markerRef = React.useRef<L.Marker | null>(null);
  const onChangeRef = React.useRef(onChange);

  React.useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);

  // Create the map once.
  React.useEffect(() => {
    if (!container.current || mapRef.current) return;
    const start: [number, number] = latitude !== null && longitude !== null ? [latitude, longitude] : DEFAULT_CENTER;
    const map = L.map(container.current, { center: start, zoom: latitude !== null ? 15 : 11, scrollWheelZoom: false });
    L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
      maxZoom: 19,
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener noreferrer">OpenStreetMap</a>',
    }).addTo(map);

    map.on("click", (event: L.LeafletMouseEvent) => onChangeRef.current(round(event.latlng.lat), round(event.latlng.lng)));
    mapRef.current = map;

    return () => {
      map.remove();
      mapRef.current = null;
      markerRef.current = null;
    };
    // The map is created once; later position changes are applied by the effect below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Keep the pin in step with the fields.
  React.useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    if (latitude === null || longitude === null) {
      markerRef.current?.remove();
      markerRef.current = null;
      return;
    }
    const position: [number, number] = [latitude, longitude];
    if (!markerRef.current) {
      const marker = L.marker(position, { icon: pinIcon, draggable: true, keyboard: false }).addTo(map);
      marker.on("dragend", () => {
        const p = marker.getLatLng();
        onChangeRef.current(round(p.lat), round(p.lng));
      });
      markerRef.current = marker;
      map.setView(position, Math.max(map.getZoom(), 15));
    } else {
      markerRef.current.setLatLng(position);
      if (!map.getBounds().contains(position)) map.panTo(position);
    }
  }, [latitude, longitude]);

  return <div ref={container} role="application" aria-label={label} className="z-0 h-72 w-full overflow-hidden rounded-card border border-line sm:h-80" />;
}

const round = (value: number) => Math.round(value * 1e6) / 1e6;
