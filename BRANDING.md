# Graphic assets

The application uses a dark anthracite surface, silver text, red accents, and
metallic frames. The note editor and interactive controls use calm backgrounds
for readable text. Control contrast is covered by the design tests.

| File | Dimensions | Purpose |
| --- | --- | --- |
| `brand-logo.png` | 1254 × 1254 px | Main emblem in the sidebar |
| `assets/icon.png` | 1254 × 1254 px | Transparent simplified emblem for EXE, tray, and overlay |
| `assets/metal.png` | 1254 × 1254 px | Main metallic background |
| `assets/header.png` | 2172 × 724 px | Project header frame with red lighting |

`make-icon.ps1` builds `app.ico` with 16, 24, 32, 48, 64, 128, and 256 px frames.
The build embeds all runtime graphics in the EXE.

The application name and version appear beside the icon in the title bar.
Search uses a drawn magnifying glass, and project names use two lines with
ellipsis when necessary. Buttons and icons are drawn in code. Scrollbars use
beveled metallic thumbs with silver edges and red accents.

The floating logo uses the PNG's per-pixel transparency through a Windows layered
window. It does not require a solid background or a color-key mask.

For replacement assets, keep the existing filenames and rebuild:

- Logo and icon: square transparent PNG, at least 1024 × 1024 px.
- Metal texture: seamless PNG, at least 1024 × 1024 px, without text.
- Header: wide PNG, at least 1600 × 500 px, without text or logos.
