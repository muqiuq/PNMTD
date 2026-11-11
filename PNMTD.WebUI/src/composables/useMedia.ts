import { onScopeDispose, ref, type Ref } from 'vue'

/** A reactive media query (the breakpoints of §5.3: 600, 960, 1200, 1600 px). */
export function useMedia(query: string): Ref<boolean> {
  const media = typeof window !== 'undefined' && window.matchMedia ? window.matchMedia(query) : null
  const matches = ref(media?.matches ?? false)
  const update = (e: MediaQueryListEvent) => (matches.value = e.matches)
  media?.addEventListener?.('change', update)
  onScopeDispose(() => media?.removeEventListener?.('change', update))
  return matches
}
