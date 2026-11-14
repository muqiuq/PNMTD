/* eslint-disable vue/one-component-per-file -- stand-ins for the components around the one tested */
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ConfirmDialog from 'primevue/confirmdialog'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, ref } from 'vue'
import { createMemoryHistory, createRouter, RouterView } from 'vue-router'
import { usePreferences } from '@/stores/preferences'
import EditingPage from './EditingPage.vue'

// docs/locations-knowledge-plan.md §5.9: the drawer as a rail while editing (the preference
// untouched), Ctrl/⌘ S, and leaving with unsaved changes asks.

const dirty = ref(false)
const saves = vi.fn()

const Editing = defineComponent({
  setup: () => () =>
    h(EditingPage, { backLabel: 'KB-42', dirty: dirty.value, onSave: saves }, { default: () => h('p', 'the editor') }),
})

async function setup() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/kb/42/edit', component: Editing },
      { path: '/kb/42', component: { render: () => h('p', 'reading') } },
    ],
  })
  await router.push('/kb/42/edit')
  const app = mount(defineComponent({ setup: () => () => [h(RouterView), h(ConfirmDialog)] }), {
    attachTo: document.body,
    global: {
      plugins: [router, [PrimeVue, { unstyled: true }], ToastService, ConfirmationService],
      directives: { tooltip: Tooltip },
    },
  })
  await flushPromises()
  return { router, app }
}

beforeEach(() => {
  setActivePinia(createPinia())
  dirty.value = false
  saves.mockReset()
})

describe('EditingPage', () => {
  it('holds the drawer as a rail while open, without changing the preference', async () => {
    const preferences = usePreferences()
    const { router, app } = await setup()
    expect(preferences.railHeld).toBe(true)
    expect(preferences.drawerCollapsed).toBe(false)
    await router.push('/kb/42')
    await flushPromises()
    expect(preferences.railHeld).toBe(false)
    expect(preferences.drawerCollapsed).toBe(false)
    app.unmount()
  })

  it('saves with Ctrl/⌘ S', async () => {
    const { app } = await setup()
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 's', ctrlKey: true, cancelable: true }))
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'S', metaKey: true, cancelable: true }))
    expect(saves).toHaveBeenCalledTimes(2)
    app.unmount()
  })

  it('leaves at once without changes, and asks first with them', async () => {
    const { router, app } = await setup()
    dirty.value = true
    await flushPromises()
    const leaving = router.push('/kb/42')
    await flushPromises()
    expect(document.body.textContent).toContain('Leave without saving?')
    Array.from(document.body.querySelectorAll('button'))
      .find((b) => b.textContent?.trim() === 'Keep editing')!
      .click()
    await leaving
    expect(router.currentRoute.value.path).toBe('/kb/42/edit')

    dirty.value = false
    await flushPromises()
    await router.push('/kb/42')
    expect(router.currentRoute.value.path).toBe('/kb/42')
    app.unmount()
  })
})
