# Icons

`favicon.svg` and `mask.svg` are the canonical sources, both authored from
`design/icon/B2-5-glow.svg`.

PNG raster favicons (`favicon-32.png`, `apple-touch-icon-180.png`) and
`favicon.ico` are generated from `favicon.svg` and committed here. Regenerate
when the source SVG changes:

```bash
# Requires librsvg + ImageMagick (macOS: brew install librsvg imagemagick).
cd public/icons
rsvg-convert -w 32  -h 32  favicon.svg -o favicon-32.png
rsvg-convert -w 180 -h 180 favicon.svg -o apple-touch-icon-180.png
rsvg-convert -w 16  -h 16  favicon.svg -o favicon-16.png
magick favicon-16.png favicon-32.png favicon.ico
```

PNG/ICO files are intentionally optional in development — the site degrades
gracefully to the SVG favicon. Add them before publishing for browsers that
don't render SVG favicons (older Safari, some Android Chromes).
