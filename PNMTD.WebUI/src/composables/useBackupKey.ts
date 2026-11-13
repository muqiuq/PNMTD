import { Decrypter, generateX25519Identity, identityToRecipient } from 'age-encryption'
import { computed, readonly, ref } from 'vue'

/**
 * The routers' backup key in this tab (docs/mikrotik-backup-plan.md §3.6): the private key, in the page's memory
 * only, never sent to PNMT and never stored. Gone on reload, on sign-out, or after 15 minutes without use.
 */
const IDLE_MS = 15 * 60_000
const identity = ref<string | null>(null)
const recipient = ref<string | null>(null)
let idle: ReturnType<typeof setTimeout> | undefined

function touch() {
  clearTimeout(idle)
  idle = setTimeout(clearBackupKey, IDLE_MS)
}

export function clearBackupKey() {
  identity.value = null
  recipient.value = null
  clearTimeout(idle)
}

/**
 * The browser's cryptography (WebCrypto) exists only in a secure context: HTTPS, or localhost. On plain HTTP the key
 * can be neither made nor used here; the age tool reads the downloaded files offline.
 */
export const cryptoAvailable = () => typeof globalThis.crypto?.subtle !== 'undefined'

export const NO_CRYPTO =
  'This page is not on HTTPS, and the browser offers its cryptography only there (or on localhost). Open PNMT over HTTPS, or decrypt the downloaded files offline with age.'

/** The secret key in a key file or a paste: the AGE-SECRET-KEY-1… line, with the comments around it ignored. */
export function secretKeyIn(text: string): string | null {
  return (
    text
      .split(/\r?\n/)
      .map((l) => l.trim())
      .find((l) => /^AGE-SECRET-KEY-1[0-9A-Z]+$/.test(l)) ?? null
  )
}

/** The public key (age1…) of a key file or a pasted key; throws when there is none or it is broken. */
export async function recipientOf(text: string): Promise<string> {
  if (!cryptoAvailable()) throw new Error(NO_CRYPTO)
  const secret = secretKeyIn(text)
  if (!secret) throw new Error('No age secret key in it: a line AGE-SECRET-KEY-1… is missing.')
  return identityToRecipient(secret)
}

/** Loads the key into this tab; its public key, to tell which backups it opens. */
export async function loadBackupKey(text: string): Promise<string> {
  const publicKey = await recipientOf(text)
  identity.value = secretKeyIn(text)
  recipient.value = publicKey
  touch()
  return publicKey
}

/** An encrypted backup was to be read, and no key is loaded: load it, then try again. */
export class NoBackupKeyError extends Error {
  constructor() {
    super('No backup key loaded.')
  }
}

/** Decrypts an age file with the loaded key. */
export async function decryptBackup(data: Uint8Array): Promise<Uint8Array> {
  if (!identity.value) throw new NoBackupKeyError()
  touch()
  const decrypter = new Decrypter()
  decrypter.addIdentity(identity.value)
  return decrypter.decrypt(data, 'uint8array')
}

/** An export's text from its gzip. */
export async function gunzip(data: Uint8Array): Promise<string> {
  const stream = new Response(data as BodyInit).body!.pipeThrough(new DecompressionStream('gzip'))
  return new Response(stream).text()
}

export function fromBase64(text: string): Uint8Array {
  return Uint8Array.from(atob(text), (c) => c.charCodeAt(0))
}

/**
 * A new key pair, made in this browser (§3.5): the key file as age-keygen writes it, for the admin to keep, and its
 * public key, the only part PNMT gets.
 */
export async function newBackupKey(): Promise<{ recipient: string; file: string }> {
  if (!cryptoAvailable()) throw new Error(NO_CRYPTO)
  const secret = await generateX25519Identity()
  const publicKey = await identityToRecipient(secret)
  const created = new Date().toISOString().replace(/\.\d+Z$/, 'Z')
  return {
    recipient: publicKey,
    file: `# PNMT's backup key: keep it safe, PNMT cannot recover it.\n# created: ${created}\n# public key: ${publicKey}\n${secret}\n`,
  }
}

export function useBackupKey() {
  return { recipient: readonly(recipient), loaded: computed(() => identity.value !== null) }
}
