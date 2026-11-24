import { call, client, type Schemas } from './client'

/**
 * Boards' calls in the admin UI (docs/boards-plan.md §12.1): the list, a board, its save, URL and preview, and the catalog. The board app
 * has a client of its own (src/board-app/client.ts). Kept beside resources.ts, as responsibilities.ts is.
 */
type S = Schemas
type Id = string

export type BoardItem = S['BoardItem']
export type BoardDetail = S['BoardDetail']
export type BoardCatalog = S['BoardCatalog']
export type BoardCatalogType = S['BoardCatalogType']
export type BoardCatalogMetric = S['BoardCatalogMetric']
export type BoardNetworkRequest = S['BoardNetworkRequest']
export type BoardTargetOption = S['BoardTargetOption']
export type BoardImageItem = S['BoardImageItem']
export type BoardMessageItem = S['BoardMessageItem']
export type BoardMessageRequest = S['BoardMessageRequest']

export const boardKeys = {
  all: ['boards'] as const,
  list: () => ['boards', 'list'] as const,
  detail: (id: Id) => ['boards', 'detail', id] as const,
  catalog: () => ['boards', 'catalog'] as const,
  address: () => ['boards', 'address'] as const,
  images: (id: Id) => ['boards', 'images', id] as const,
  messages: (open: boolean) => ['boards', 'messages', open ? 'open' : 'recent'] as const,
}

export const boardsApi = {
  list: () => call(client.GET('/api/v1/boards')),
  get: (id: Id) => call(client.GET('/api/v1/boards/{id}', { params: { path: { id } } })),
  create: (body: S['BoardCreateRequest']) => call(client.POST('/api/v1/boards', { body })),
  /** The whole page at once: 409 edit_conflict, 403 element_right. */
  save: (id: Id, body: S['BoardRequest']) =>
    call(client.PUT('/api/v1/boards/{id}', { params: { path: { id } }, body })),
  duplicate: (id: Id) => call(client.POST('/api/v1/boards/{id}/duplicate', { params: { path: { id } } })),
  remove: (id: Id) => call(client.DELETE('/api/v1/boards/{id}', { params: { path: { id } } })),
  /** Shown: audited. */
  url: (id: Id) => call(client.GET('/api/v1/boards/{id}/url', { params: { path: { id } } })),
  /** A new URL; the old one stops at once. */
  renew: (id: Id) => call(client.POST('/api/v1/boards/{id}/url/renew', { params: { path: { id } } })),
  preview: (body: S['BoardPreviewRequest']) => call(client.POST('/api/v1/boards/preview', { body })),
  catalog: () => call(client.GET('/api/v1/boards/catalog')),
  targets: (q: string) => call(client.GET('/api/v1/boards/catalog/targets', { params: { query: { q } } })),
  names: (id: Id[]) => call(client.GET('/api/v1/boards/catalog/names', { params: { query: { id } } })),
  address: () => call(client.GET('/api/v1/boards/address')),
  /** The images a board uses, newest first (docs/board-messages-plan.md §9.4). */
  images: (id: Id) => call(client.GET('/api/v1/boards/{id}/images', { params: { path: { id } } })),
  /** Board messages (docs/board-messages-plan.md §7): the open ones, or the last sent. */
  messages: (open: boolean, limit = 20) =>
    call(client.GET('/api/v1/boards/messages', { params: { query: { open, limit } } })),
  /** 409 board_disabled, board_no_messages, too_many_open; 429 with the Debug page's limit. */
  sendMessage: (body: BoardMessageRequest) => call(client.POST('/api/v1/boards/messages', { body })),
  withdrawMessage: (id: Id) => call(client.POST('/api/v1/boards/messages/{id}/withdraw', { params: { path: { id } } })),
}

/** One of a board's images in the admin UI: the session's cookie is enough for an <img>. */
export const boardImageUrl = (id: Id, fileId: Id) => `/api/v1/boards/${id}/images/${fileId}`
