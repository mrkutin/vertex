import { defineCollection, z } from 'astro:content';

/**
 * "pages" collection — long-form MDX content (Privacy, Terms, Support …).
 * Filename convention: src/content/pages/<slug>/<lang>.mdx
 *   slug = page id (`privacy`, `terms`, `support`)
 *   lang = `en` | `ru`
 */
const pages = defineCollection({
  type: 'content',
  schema: z.object({
    title: z.string(),
    description: z.string().optional(),
    lastUpdated: z.string().optional(),
  }),
});

export const collections = {
  pages,
};
