import { flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { usePreferences } from '@/stores/preferences'
import { mountWithUi } from '@/test/mount'
import MarkdownEditor from './MarkdownEditor.vue'

// docs/locations-knowledge-plan.md §5.4, R17: where an editor's chunk doesn't load, the textarea takes
// over, with a note and without the switch.
vi.mock('./VisualEditor.vue', () => {
  throw new Error('blocked chunk')
})
vi.mock('./CodeEditor.vue', () => {
  throw new Error('blocked chunk')
})

beforeEach(() => {
  setActivePinia(createPinia())
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('min-width: 960px'),
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }))
  vi.spyOn(console, 'error').mockImplementation(() => {})
  vi.spyOn(console, 'warn').mockImplementation(() => {})
})

describe('the fallback', () => {
  it.each(['visual', 'markdown'] as const)('edits as plain text when the %s editor can’t load', async (mode) => {
    usePreferences().editorModes.page = mode
    const update = vi.fn()
    const editor = mountWithUi(MarkdownEditor, {
      props: { modelValue: '# Hi', mode: 'page', layout: 'inline', 'onUpdate:modelValue': update },
      attachTo: document.body,
      global: { plugins: [[PrimeVue, { unstyled: true }], ToastService, ConfirmationService] },
    })
    await flushPromises()
    await flushPromises()
    const textarea = editor.find('textarea.md-textarea')
    expect(textarea.exists()).toBe(true)
    expect(editor.text()).toContain('The editor couldn’t start; editing as plain text.')
    expect(editor.find('[aria-label="Editor"]').exists()).toBe(false)
    await textarea.setValue('# Hi there')
    expect(update).toHaveBeenCalledWith('# Hi there')
    // Preview stays.
    expect(editor.findAll('button').some((b) => b.text() === 'Preview')).toBe(true)
    editor.unmount()
  })
})
