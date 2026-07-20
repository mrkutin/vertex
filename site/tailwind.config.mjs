/** @type {import('tailwindcss').Config} */
export default {
  content: ['./src/**/*.{astro,html,js,jsx,md,mdx,ts,tsx}'],
  darkMode: ['selector', '[data-theme="dark"]'],
  theme: {
    extend: {
      colors: {
        canvas: {
          DEFAULT: 'var(--vx-bg-canvas)',
          top: 'var(--vx-bg-canvas-top)',
          bottom: 'var(--vx-bg-canvas-bottom)',
        },
        surface: {
          DEFAULT: 'var(--vx-bg-surface)',
          elev: 'var(--vx-bg-surface-elev)',
          muted: 'var(--vx-bg-surface-muted)',
        },
        accent: {
          DEFAULT: 'var(--vx-accent)',
          hover: 'var(--vx-accent-hover)',
          soft: 'var(--vx-accent-soft)',
          on: 'var(--vx-text-on-accent)',
        },
        text: {
          primary: 'var(--vx-text-primary)',
          secondary: 'var(--vx-text-secondary)',
          tertiary: 'var(--vx-text-tertiary)',
        },
        border: {
          subtle: 'var(--vx-border-subtle)',
          strong: 'var(--vx-border-strong)',
        },
        state: {
          connected: 'var(--vx-state-connected)',
          warn: 'var(--vx-state-warn)',
          error: 'var(--vx-state-error)',
        },
      },
      fontFamily: {
        sans: ['"Inter Variable"', 'Inter', 'system-ui', '-apple-system', 'Segoe UI', 'Roboto', 'sans-serif'],
        mono: ['"JetBrains Mono"', 'ui-monospace', 'SFMono-Regular', 'Menlo', 'Consolas', 'monospace'],
        wordmark: ['"Inter Variable"', 'Inter', 'system-ui', 'sans-serif'],
      },
      letterSpacing: {
        wordmark: '0.15em',
      },
      fontSize: {
        display: 'var(--vx-fs-display)',
        h1: 'var(--vx-fs-h1)',
        h2: 'var(--vx-fs-h2)',
        h3: 'var(--vx-fs-h3)',
        body: 'var(--vx-fs-body)',
        small: 'var(--vx-fs-small)',
        mono: 'var(--vx-fs-mono)',
      },
      maxWidth: {
        prose: '65ch',
        content: '720px',
        wide: '1180px',
      },
      backdropBlur: {
        header: '18px',
      },
      boxShadow: {
        glow: '0 0 80px var(--vx-glow-primary)',
        'glow-sm': '0 0 32px var(--vx-glow-primary)',
      },
    },
  },
  plugins: [],
};
