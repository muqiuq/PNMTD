import type { InjectionKey, Ref } from 'vue'

/**
 * What a board's elements get from where the board is drawn (docs/board-messages-plan.md §9.4): how images are fetched (by the board's
 * token in the board app, by the session in the admin's preview), and whether the board moves (its Motion setting and the screen's
 * prefers-reduced-motion).
 */
export type ImageLoader = (fileId: string) => Promise<Blob>

export const imageLoaderKey: InjectionKey<ImageLoader | null> = Symbol('boardImages')

export const motionKey: InjectionKey<Ref<boolean>> = Symbol('boardMotion')
