/* eslint-disable vue/one-component-per-file -- stand-ins for the components around the one tested */
import { flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, nextTick } from 'vue'
import { usePreferences } from '@/stores/preferences'
import { mountWithUi } from '@/test/mount'
import MarkdownEditor from './MarkdownEditor.vue'
import type { MarkdownSave } from './types'

// The two editors are their own chunks; here, stand-ins that behave as their contracts say. The
// visual one writes `-` where the text has `*` (as Crepe does, §5.4).
vi.mock('./VisualEditor.vue', () => ({
  __esModule: true,
  default: defineComponent({
    props: { modelValue: { type: String, required: true } },
    emits: ['ready', 'update:modelValue'],
    setup: (props) => () => h('div', { class: 'visual-stand-in' }, props.modelValue),
    mounted() {
      this.$emit('ready', this.modelValue.replace(/^\* /gm, '- '))
    },
  }),
}))
vi.mock('./CodeEditor.vue', () => ({
  __esModule: true,
  default: defineComponent({
    props: { modelValue: { type: String, required: true } },
    emits: ['update:modelValue'],
    setup:
      (props, { emit }) =>
      () =>
        h('textarea', {
          class: 'code-stand-in',
          value: props.modelValue,
          onInput: (e: Event) => emit('update:modelValue', (e.target as HTMLTextAreaElement).value),
        }),
  }),
}))
vi.mock('./MonacoDiff.vue', () => ({
  __esModule: true,
  default: defineComponent({ setup: () => () => h('div', { class: 'diff' }) }),
}))

let phone = false
beforeEach(() => {
  setActivePinia(createPinia())
  localStorage.clear()
  phone = false
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('max-width: 599px') ? phone : query.includes('min-width: 960px'),
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }))
})

afterEach(() => {
  document.body.innerHTML = ''
})

type Props = InstanceType<typeof MarkdownEditor>['$props']

function mountEditor(props: Partial<Props> & { modelValue: string }) {
  return mountWithUi(MarkdownEditor, {
    props: { mode: 'page', layout: 'inline', 'onUpdate:modelValue': () => {}, ...props } as Props,
    attachTo: document.body,
    global: { plugins: [[PrimeVue, { unstyled: true }], ToastService, ConfirmationService] },
  })
}

const button = (label: string) =>
  Array.from(document.body.querySelectorAll('button')).find((b) => b.textContent?.trim() === label)

describe('MarkdownEditor', () => {
  it('opens in the remembered mode, apart for pages and fields', async () => {
    usePreferences().editorModes = { page: 'markdown', field: 'visual' }
    const page = mountEditor({ modelValue: 'text' })
    await flushPromises()
    expect(page.find('.code-stand-in').exists()).toBe(true)
    const field = mountEditor({ modelValue: 'text', mode: 'field' })
    await flushPromises()
    expect(field.find('.visual-stand-in').exists()).toBe(true)
  })

  it('uses the textarea for the Markdown mode on a phone', async () => {
    phone = true
    usePreferences().editorModes.page = 'markdown'
    const editor = mountEditor({ modelValue: '# Hi' })
    await flushPromises()
    expect(editor.find('textarea.md-textarea').exists()).toBe(true)
    expect(editor.find('.code-stand-in').exists()).toBe(false)
  })

  it('switches to Visual at once when nothing would be rewritten', async () => {
    usePreferences().editorModes.page = 'markdown'
    const editor = mountEditor({ modelValue: '- a\n- b\n' })
    await flushPromises()
    ;(editor.vm as unknown as { $: { setupState: { setMode(m: string): void } } }).$.setupState.setMode('visual')
    await flushPromises()
    expect(usePreferences().editorModes.page).toBe('visual')
    expect(document.body.textContent).not.toContain('will rewrite')
  })

  it('asks before the visual editor rewrites lines, and stays when told to', async () => {
    usePreferences().editorModes.page = 'markdown'
    const editor = mountEditor({ modelValue: '* a\n* b\n\ntext\n' })
    await flushPromises()
    ;(editor.vm as unknown as { $: { setupState: { setMode(m: string): void } } }).$.setupState.setMode('visual')
    await flushPromises()
    expect(document.body.textContent).toContain('The visual editor will rewrite 2 lines')
    button('Stay')!.click()
    await flushPromises()
    expect(usePreferences().editorModes.page).toBe('markdown')
    expect(editor.find('.visual-stand-in').exists()).toBe(false)
  })

  it('switches anyway, leaving the text as it was until it is edited', async () => {
    usePreferences().editorModes.page = 'markdown'
    const update = vi.fn()
    const editor = mountEditor({ modelValue: '* a\n', 'onUpdate:modelValue': update })
    await flushPromises()
    ;(editor.vm as unknown as { $: { setupState: { setMode(m: string): void } } }).$.setupState.setMode('visual')
    await flushPromises()
    button('Switch anyway')!.click()
    await flushPromises()
    expect(usePreferences().editorModes.page).toBe('visual')
    expect(editor.find('.visual-stand-in').isVisible()).toBe(true)
    expect(update).not.toHaveBeenCalled()
  })

  it('saves with Ctrl/⌘ S, carrying the base revision', async () => {
    const save = vi.fn<(s: MarkdownSave) => void>()
    const editor = mountEditor({ modelValue: 'text', baseRevision: 7, onSave: save })
    await flushPromises()
    await editor.find('section').trigger('keydown', { key: 's', ctrlKey: true })
    expect(save).toHaveBeenCalledWith({
      markdown: 'text',
      revision: 7,
      overwrite: false,
      acknowledgeCredentials: false,
      markSensitive: false,
    })
  })

  it('offers a draft from a crash, and a restored draft from an older revision saves against that one', async () => {
    localStorage.setItem(
      'pnmt.draft.kb:1',
      JSON.stringify({ revision: 3, text: 'my lost text', savedAt: new Date().toISOString() }),
    )
    const update = vi.fn()
    const save = vi.fn<(s: MarkdownSave) => void>()
    const editor = mountEditor({
      modelValue: 'saved text',
      baseRevision: 5,
      draftKey: 'kb:1',
      'onUpdate:modelValue': update,
      onSave: save,
    })
    await flushPromises()
    expect(document.body.textContent).toContain('You have unsaved changes from')
    button('Restore')!.click()
    expect(update).toHaveBeenCalledWith('my lost text')
    await editor.setProps({ modelValue: 'my lost text' })
    await editor.find('section').trigger('keydown', { key: 's', metaKey: true })
    expect(save.mock.calls[0]![0]).toMatchObject({ markdown: 'my lost text', revision: 3 })
  })

  it('keeps a draft while the text differs, and clears it once saved', async () => {
    vi.useFakeTimers()
    const editor = mountEditor({ modelValue: 'start', draftKey: 'field:x', baseRevision: 1 })
    await editor.setProps({ modelValue: 'changed' })
    vi.advanceTimersByTime(1000)
    expect(JSON.parse(localStorage.getItem('pnmt.draft.field:x')!)).toMatchObject({ text: 'changed', revision: 1 })
    ;(editor.vm as unknown as { saved(): void }).saved()
    expect(localStorage.getItem('pnmt.draft.field:x')).toBeNull()
    vi.useRealTimers()
  })

  it('reports unsaved changes, the details’ too', async () => {
    const dirty = vi.fn()
    const editor = mountEditor({ modelValue: 'a', onDirty: dirty })
    await editor.setProps({ modelValue: 'b' })
    expect(dirty).toHaveBeenLastCalledWith(true)
    await editor.setProps({ modelValue: 'a' })
    expect(dirty).toHaveBeenLastCalledWith(false)
    await editor.setProps({ detailsDirty: true })
    expect(dirty).toHaveBeenLastCalledWith(true)
  })

  it('offers Save mine anyway after a conflict, on top of their revision', async () => {
    const save = vi.fn<(s: MarkdownSave) => void>()
    const conflict = vi.fn()
    mountEditor({
      modelValue: 'mine',
      baseRevision: 4,
      onSave: save,
      conflict: { by: 'Anna Muster', at: '2026-11-14T10:42:00Z', revision: 5, theirs: 'theirs' },
      'onUpdate:conflict': conflict,
    })
    await flushPromises()
    expect(document.body.textContent).toContain('Anna Muster saved at')
    button('Save mine anyway')!.click()
    await nextTick()
    expect(conflict).toHaveBeenCalledWith(null)
    expect(save.mock.calls[0]![0]).toMatchObject({ markdown: 'mine', revision: 5, overwrite: true })
  })

  it('warns about a credential, and saves anyway or marked sensitive when told to', async () => {
    const save = vi.fn<(s: MarkdownSave) => void>()
    const editor = mountEditor({
      modelValue: 'password: hunter2',
      onSave: save,
      canMarkSensitive: true,
      credentials: { lines: [12], kinds: ['a password'] },
    })
    await flushPromises()
    expect(document.body.textContent).toContain('Line 12 looks like a password. PNMT is not a password manager.')
    button('Mark as sensitive and save')!.click()
    expect(save.mock.calls[0]![0]).toMatchObject({ acknowledgeCredentials: true, markSensitive: true })

    await editor.setProps({ credentials: { lines: [3, 9] } })
    await flushPromises()
    expect(document.body.textContent).toContain('Lines 3 and 9 look like credentials.')
    button('Save anyway')!.click()
    expect(save.mock.calls[1]![0]).toMatchObject({ acknowledgeCredentials: true, markSensitive: false })
  })

  it('refuses pasted files where uploads are off', async () => {
    phone = true
    usePreferences().editorModes.page = 'markdown'
    const editor = mountEditor({ modelValue: '', uploads: false })
    await flushPromises()
    const file = new File(['x'], 'shot.png', { type: 'image/png' })
    const paste = new Event('paste', { bubbles: true, cancelable: true }) as Event & { clipboardData: unknown }
    paste.clipboardData = { files: [file], getData: () => '' }
    editor.find('textarea').element.dispatchEvent(paste)
    await flushPromises()
    expect(document.body.textContent).toContain('Files can’t be added here.')
  })
})
