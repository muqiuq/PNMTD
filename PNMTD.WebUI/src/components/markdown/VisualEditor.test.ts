import { flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mountWithUi } from '@/test/mount'
import VisualEditor from './VisualEditor.vue'

class NoObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
  takeRecords() {
    return []
  }
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.stubGlobal('IntersectionObserver', NoObserver)
  vi.stubGlobal('ResizeObserver', NoObserver)
})

async function open(modelValue: string) {
  const update = vi.fn()
  const pending = vi.fn()
  const ready = vi.fn()
  const editor = mountWithUi(VisualEditor, {
    props: { modelValue, upload: null, 'onUpdate:modelValue': update, onPending: pending, onReady: ready },
    attachTo: document.body,
  })
  await vi.waitFor(() => expect(ready).toHaveBeenCalled())
  const vm = editor.vm as unknown as { flush(): string; insertMarkdown(text: string): void }
  return { editor, vm, update, pending, ready }
}

// Milkdown reports changes 200 ms late; a save, a switch or leaving right after typing must still see
// them (docs/locations-knowledge-plan.md §5.4, §5.7).
describe('VisualEditor', () => {
  it('says at once that there are edits, and writes them on flush', async () => {
    const { editor, vm, update, pending } = await open('# Hello\n')
    vm.insertMarkdown(' world')
    expect(pending).toHaveBeenLastCalledWith(true)
    expect(update).not.toHaveBeenCalled()
    const text = vm.flush()
    expect(text).toContain('world')
    expect(update).toHaveBeenLastCalledWith(text)
    expect(pending).toHaveBeenLastCalledWith(false)
    editor.unmount()
  })

  it('writes its edits when it closes', async () => {
    const { editor, vm, update } = await open('Text\n')
    vm.insertMarkdown(' more')
    editor.unmount()
    expect(update.mock.lastCall?.[0]).toContain('more')
  })

  it('leaves the text as it came until it is edited', async () => {
    const { editor, vm, update, ready } = await open('* one\n* two\n')
    expect(ready).toHaveBeenCalledWith('- one\n- two\n')
    expect(vm.flush()).toBe('* one\n* two\n')
    await flushPromises()
    expect(update).not.toHaveBeenCalled()
    editor.unmount()
  })

  it('carries in a text that changed while it started', async () => {
    const update = vi.fn()
    const ready = vi.fn()
    const editor = mountWithUi(VisualEditor, {
      props: { modelValue: 'first\n', upload: null, 'onUpdate:modelValue': update, onReady: ready },
      attachTo: document.body,
    })
    await editor.setProps({ modelValue: 'typed while it started\n' })
    await vi.waitFor(() => expect(ready).toHaveBeenCalled())
    expect(ready).toHaveBeenCalledWith('typed while it started\n')
    const vm = editor.vm as unknown as { flush(): string }
    expect(vm.flush()).toBe('typed while it started\n')
    expect(editor.text()).toContain('typed while it started')
    expect(update).not.toHaveBeenCalled()
    editor.unmount()
  })
})
