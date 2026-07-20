# Self-hosted fonts

The site renders **Inter Variable** for UI/text and **JetBrains Mono Variable**
for monospaced accents. Both are loaded with `font-display: swap` from this
folder. **No Google Fonts CDN, no external script** — keep it that way.

## Files expected here

```
public/fonts/
├── Inter-Variable.woff2          (~340 KB — full variable axis)
└── JetBrainsMono-Variable.woff2  (~170 KB)
```

Until you drop the woff2 files in this directory the site falls back to
`system-ui` / `ui-monospace`, so dev still works — just without brand
typography.

## Where to download

### Inter Variable
- Source: <https://rsms.me/inter/>
- File: `Inter.zip` → `Inter Variable/Inter-VariableFont_opsz,wght.ttf`
- Convert to woff2 (subset to Latin + Cyrillic if you want a smaller file).
  The straightforward path:
  ```bash
  # macOS: brew install woff2 fonttools
  pyftsubset Inter-VariableFont_opsz,wght.ttf \
    --unicodes='U+0000-007F,U+00A0-024F,U+0400-04FF,U+0500-052F,U+2010-2027,U+2030-205E' \
    --layout-features='*' \
    --flavor=woff2 \
    --output-file=Inter-Variable.woff2
  ```
- Or grab a ready-made build from the [Inter v4 release](https://github.com/rsms/inter/releases)
  and copy `Inter-roman.var.woff2`, renaming to `Inter-Variable.woff2`.

### JetBrains Mono Variable
- Source: <https://github.com/JetBrains/JetBrainsMono/releases>
- File: `fonts/variable/JetBrainsMono[wght].ttf`
- Convert to woff2:
  ```bash
  pyftsubset JetBrainsMono\[wght\].ttf \
    --unicodes='U+0000-007F,U+00A0-024F,U+0400-04FF,U+2010-2027' \
    --layout-features='*' \
    --flavor=woff2 \
    --output-file=JetBrainsMono-Variable.woff2
  ```

## Licensing

- Inter — SIL Open Font License 1.1
- JetBrains Mono — SIL Open Font License 1.1

Both can be redistributed with the site bundle. Drop their `OFL.txt` files
under `public/fonts/licenses/` if you want to keep the attribution next to the
font files.
