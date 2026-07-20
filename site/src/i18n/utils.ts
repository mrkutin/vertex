import { defaultLang, ui, type Lang, type UIKey } from './ui';

/**
 * Extract current language from URL pathname.
 * `/ru/...` -> 'ru', anything else -> 'en'.
 */
export function getLangFromUrl(url: URL): Lang {
  const [, maybeLang] = url.pathname.split('/');
  if (maybeLang && maybeLang in ui) {
    return maybeLang as Lang;
  }
  return defaultLang;
}

/**
 * Returns a translator bound to the supplied language. Falls back to English
 * if the key is missing in the localized dictionary.
 */
export function useTranslations(lang: Lang) {
  return function t(key: UIKey): string {
    return ui[lang][key] ?? ui[defaultLang][key];
  };
}

/**
 * Normalize a path so it always ends with `/`, except when it already points
 * at a file with an extension (e.g., `/foo.svg`, `/sitemap-index.xml`).
 */
function withTrailingSlash(path: string): string {
  if (!path) return '/';
  // Don't add slash to file-like paths or anchors / query strings.
  const lastSegment = path.split(/[?#]/)[0]!.split('/').pop() ?? '';
  const hasExtension = /\.[a-zA-Z0-9]{1,8}$/.test(lastSegment);
  if (hasExtension) return path;
  return path.endsWith('/') ? path : `${path}/`;
}

/**
 * Build a path that points at the same logical resource in the supplied
 * language. EN is unprefixed, RU is `/ru/...`. Always trailing-slash terminated.
 */
export function useTranslatedPath(lang: Lang) {
  return function translatePath(path: string): string {
    const clean = path.startsWith('/') ? path : `/${path}`;
    if (lang === defaultLang) {
      return withTrailingSlash(clean);
    }
    if (clean === '/' || clean === '') {
      return `/${lang}/`;
    }
    return withTrailingSlash(`/${lang}${clean}`);
  };
}

/**
 * Strip the locale prefix from a path so it can be re-prefixed elsewhere.
 * `/ru/features/` -> `/features/`, `/features/` -> `/features/`.
 */
export function stripLangPrefix(pathname: string): string {
  const parts = pathname.split('/');
  if (parts.length > 1 && parts[1] && parts[1] in ui && parts[1] !== defaultLang) {
    const stripped = '/' + parts.slice(2).join('/');
    return stripped === '/' ? '/' : stripped;
  }
  return pathname;
}

/**
 * Compute the URL of the same page in the alternate locale.
 */
export function alternatePath(currentUrl: URL, target: Lang): string {
  const stripped = stripLangPrefix(currentUrl.pathname);
  return useTranslatedPath(target)(stripped || '/');
}
