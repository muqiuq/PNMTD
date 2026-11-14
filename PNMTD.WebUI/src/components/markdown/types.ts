/**
 * The types the Markdown components share with the views built on them (docs/locations-knowledge-plan.md
 * §5, §10.2). Kept apart from the components so a view can import them without loading an editor.
 */

/** A file of PNMT's store, as the page's response describes it (§5.5, §5.6). */
export interface MarkdownFile {
  id: string
  name: string
  size: number
  contentType: string
  kind?: 'Image' | 'Attachment' | 'image' | 'attachment'
  /** Removed by an Admin (§5.5): the page shows *File removed*. */
  deleted?: boolean
}

/** Which remembered editor mode applies (§5.4): pages and location fields keep their own. */
export type EditorModeKey = 'page' | 'field'

/** The two editors behind the switch (§5.4). */
export type { EditorMode } from '@/stores/preferences'

/** A revision as the API names it: KB pages count, location values too. */
export type Revision = number | string

/**
 * A save's content, from Save, Ctrl/⌘ S or one of the dialogs (§5.7). The view sends `markdown` with
 * `revision`, and the flags as its API wants them.
 */
export interface MarkdownSave {
  markdown: string
  /**
   * The revision the edit started from: the editor's baseRevision; a restored draft's when it began
   * on an older one, so that the server answers 409 instead of overwriting; for *Save mine anyway*
   * the conflict's revision.
   */
  revision: Revision | null
  /** *Save mine anyway* after a conflict: a new revision on top of theirs (§5.7). */
  overwrite: boolean
  /** The credentials warning was answered with *Save anyway* or *Mark as sensitive and save*. */
  acknowledgeCredentials: boolean
  /** *Mark as sensitive and save* (pages only, §7.1). */
  markSensitive: boolean
}

/** A 409 edit_conflict, as the view passes it to the editor (§5.7). */
export interface EditConflict {
  /** Who saved in between, and when (ISO 8601). */
  by: string
  at: string
  /** Their revision: *Save mine anyway* saves on top of it. */
  revision: Revision
  /** Their Markdown, for *Their changes*. */
  theirs: string
  /** The Markdown the edit started from; the text the editor was opened with when left out. */
  base?: string
}

/** What the credentials scan found (§5.7, §7.3): line numbers, never the text. */
export interface CredentialsWarning {
  /** 1-based lines that look like credentials. */
  lines: number[]
  /** What each looks like, where known, in the same order: "a password", "a private key". */
  kinds?: string[]
}

/** A link *Link to a page…* inserts (§6.5): `[label](href)`. */
export interface PageLink {
  label: string
  href: string
}
