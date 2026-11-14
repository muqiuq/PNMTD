import { ApiError, call, client, type Schemas } from './client'

/**
 * The knowledge base's calls (docs/locations-knowledge-plan.md §9), and the Admin's files overview it shows (§10.6):
 * pages by number to read, by id to change. Kept beside resources.ts, which holds the older resources.
 */

type S = Schemas
type Id = string

export type KbPage = S['KbPageDetail']
export type KbListItem = S['KbPageListItem']
export type KbPageRequest = S['KbPageRequest']
export type KbStatus = S['KbPageStatus']
export type KbLinkKind = S['KbLinkKind']
export type KbLink = S['KbLinkItem']
export type KbRevision = S['KbRevisionItem']
export type KbChange = S['KbChange']
export type KbTag = S['KbTagItem']
export type KbStatements = S['KbStatements']
export type KbAiStatus = S['KbAiTagsStatus']
export type KbRelatedGroup = S['KbRelatedGroup']

/** The list's query (§6.6): what the URL holds, as the API takes it. */
export interface KbListQuery {
  q?: string
  tags?: string
  status?: KbStatus
  location?: Id
  host?: Id
  sensor?: Id
  archived?: boolean
  sort?: string
  period?: string
  page?: number
  pageSize?: number
}

export const kbApi = {
  list: (query: KbListQuery) => call(client.GET('/api/v1/kb/pages', { params: { query } })),
  get: (number: number) => call(client.GET('/api/v1/kb/pages/{number}', { params: { path: { number } } })),
  create: (body: KbPageRequest) => call(client.POST('/api/v1/kb/pages', { body })),
  update: (id: Id, body: KbPageRequest) =>
    call(client.PUT('/api/v1/kb/pages/{id}', { params: { path: { id } }, body })),
  archive: (id: Id) => call(client.POST('/api/v1/kb/pages/{id}/archive', { params: { path: { id } } })),
  unarchive: (id: Id) => call(client.POST('/api/v1/kb/pages/{id}/restore', { params: { path: { id } } })),
  remove: (id: Id) => call(client.DELETE('/api/v1/kb/pages/{id}', { params: { path: { id } } })),
  /** Once per opening of the page (§6.6); the server counts a person once per 30 minutes. */
  view: (id: Id) => call(client.POST('/api/v1/kb/pages/{id}/views', { params: { path: { id } } })),
  editing: (id: Id, stopped = false) =>
    call(client.POST('/api/v1/kb/pages/{id}/editing', { params: { path: { id } }, body: { stopped } })),
  revisions: (id: Id) => call(client.GET('/api/v1/kb/pages/{id}/revisions', { params: { path: { id } } })),
  revision: (id: Id, number: number) =>
    call(client.GET('/api/v1/kb/pages/{id}/revisions/{number}', { params: { path: { id, number } } })),
  diff: (id: Id, from?: number, to?: number) =>
    call(client.GET('/api/v1/kb/pages/{id}/diff', { params: { path: { id }, query: { from, to } } })),
  restoreRevision: (id: Id, number: number) =>
    call(client.POST('/api/v1/kb/pages/{id}/revisions/{number}/restore', { params: { path: { id, number } } })),
  redact: (id: Id, text: string) =>
    call(client.POST('/api/v1/kb/pages/{id}/redact', { params: { path: { id } }, body: { text } })),
  sensitive: (id: Id, body: S['KbSensitiveRequest']) =>
    call(client.PUT('/api/v1/kb/pages/{id}/sensitive', { params: { path: { id } }, body })),
  startAiTags: (id: Id, body: S['KbAiTagsRequest']) =>
    call(client.POST('/api/v1/kb/pages/{id}/ai-tags', { params: { path: { id } }, body })),
  aiTags: (id: Id, workId: Id) =>
    call(client.GET('/api/v1/kb/pages/{id}/ai-tags/{workId}', { params: { path: { id, workId } } })),
  related: (query: { locationId?: Id; hostId?: Id; sensorId?: Id }) =>
    call(client.GET('/api/v1/kb/related', { params: { query } })),
  tags: (search?: string) => call(client.GET('/api/v1/kb/tags', { params: { query: { search } } })),
  renameTag: (id: Id, name: string) =>
    call(client.PUT('/api/v1/kb/tags/{id}', { params: { path: { id } }, body: { name } })),
  removeTag: (id: Id) => call(client.DELETE('/api/v1/kb/tags/{id}', { params: { path: { id } } })),

  /** Admin → Knowledge base → Files (§10.6). */
  files: (query: {
    kind?: S['FileKind']
    unused?: boolean
    unknown?: boolean
    page?: number
    pageSize?: number
    sort?: string
  }) => call(client.GET('/api/v1/files', { params: { query } })),
  removeFile: (id: Id) => call(client.DELETE('/api/v1/files/{id}', { params: { path: { id } } })),
}

/** Query keys: everything of the knowledge base under one prefix, so a change refreshes its lists and panels. */
export const kbKeys = {
  all: ['kb'] as const,
  list: (query: KbListQuery) => ['kb', 'list', query] as const,
  page: (number: number) => ['kb', 'page', number] as const,
  revisions: (id: Id) => ['kb', 'revisions', id] as const,
  related: (query: { locationId?: Id; hostId?: Id; sensorId?: Id }) => ['kb', 'related', query] as const,
  tags: ['kb', 'tags'] as const,
  files: ['kb-files'] as const,
}

/** The request that saves a page as it is, with some fields changed: PUT takes every writable field. */
export function kbRequest(page: KbPage, patch: Partial<KbPageRequest> = {}): KbPageRequest {
  return {
    title: page.title,
    summary: page.summary ?? null,
    content: page.content,
    tags: page.tags.map((t) => t.name),
    aiTags: page.tags.filter((t) => t.source === 'Ai').map((t) => t.name),
    status: page.status,
    links: page.links.filter((l) => l.exists).map((l) => ({ kind: l.kind, id: l.id })),
    revision: page.revision,
    note: null,
    acknowledgeCredentials: false,
    markSensitive: false,
    ...patch,
  }
}

/** KB-42 */
export const kbNumber = (number: number) => `KB-${number}`

export const kbStatusLabel: Record<KbStatus, string> = {
  Draft: 'Draft',
  Published: 'Published',
  NeedsReview: 'Needs review',
}

export const kbStatusOptions: { label: string; value: KbStatus }[] = [
  { label: 'Published', value: 'Published' },
  { label: 'Draft', value: 'Draft' },
  { label: 'Needs review', value: 'NeedsReview' },
]

/** PrimeVue Tag severities by status: Published needs no mark. */
export function kbStatusSeverity(status: KbStatus): 'secondary' | 'warn' | undefined {
  return status === 'Draft' ? 'secondary' : status === 'NeedsReview' ? 'warn' : undefined
}

/** What a revision's change list item is called (§6.3). */
export const kbChangeLabel: Record<string, string> = {
  created: 'Created',
  content: 'Content',
  title: 'Title',
  summary: 'Summary',
  tags: 'Tags',
  status: 'Status',
  links: 'Links',
  files: 'Files added',
  sensitive: 'Sensitive',
  archived: 'Archive',
  restored: 'Restored',
  aiTags: 'AI tags',
  redacted: 'Redacted',
}

export const kbLinkIcon: Record<KbLinkKind, string> = {
  Location: 'pi pi-map-marker',
  Host: 'pi pi-server',
  Sensor: 'pi pi-wave-pulse',
}

/** Where a link opens in the UI. */
export function kbLinkRoute(link: { kind: KbLinkKind; id: string }) {
  return link.kind === 'Location'
    ? { name: 'location', params: { id: link.id } }
    : link.kind === 'Host'
      ? { name: 'host', params: { id: link.id } }
      : { name: 'sensor', params: { id: link.id } }
}

/** A 422 looks_like_credential's line numbers, 0 for the title or summary; never the text (§7.3). */
export function credentialLines(error: unknown): number[] | null {
  if (!(error instanceof ApiError) || error.code !== 'looks_like_credential') return null
  const lines = error.body.lines
  return Array.isArray(lines) ? lines.filter((l): l is number => typeof l === 'number') : []
}

/** What a 422 looks_like_credential found per line ("password", "privateKey"), in the order of its lines. */
export function credentialKinds(error: unknown): string[] {
  const kinds = error instanceof ApiError ? error.body.kinds : undefined
  return Array.isArray(kinds) ? kinds.filter((k): k is string => typeof k === 'string') : []
}

/** "Lines 2 and 12", "Line 4", "The title or summary". */
export function linesText(lines: readonly number[]): string {
  const numbered = [...new Set(lines.filter((l) => l > 0))]
  const titled = lines.some((l) => l <= 0)
  const parts: string[] = []
  if (numbered.length === 1) parts.push(`Line ${numbered[0]}`)
  else if (numbered.length > 1)
    parts.push(`Lines ${numbered.slice(0, -1).join(', ')} and ${numbered[numbered.length - 1]}`)
  if (titled) parts.push(parts.length ? 'the title or summary' : 'The title or summary')
  return parts.join(' and ')
}
