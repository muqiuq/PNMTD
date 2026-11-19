import { describe, expect, it, vi } from 'vitest'
import { defineComponent, ref } from 'vue'
import type { S } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import ScriptArgumentInput from './ScriptArgumentInput.vue'

vi.stubGlobal('matchMedia', (query: string) => ({
  matches: false,
  media: query,
  addEventListener: () => {},
  removeEventListener: () => {},
}))
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
)

const parameter = (p: Partial<S['ScriptParameter']>): S['ScriptParameter'] => ({
  name: 'x',
  type: 'string',
  mandatory: false,
  default: null,
  position: null,
  validateSet: null,
  rangeMin: null,
  rangeMax: null,
  helpMessage: null,
  isSwitch: false,
  isSecure: false,
  ...p,
})

function mount(p: S['ScriptParameter'], start: unknown = undefined, multiline = false) {
  const value = ref<unknown>(start)
  const wrapper = mountWithUi(
    defineComponent({
      components: { ScriptArgumentInput },
      setup: () => ({ value, p, multiline }),
      template: '<ScriptArgumentInput id="arg" v-model="value" :parameter="p" :multiline="multiline" />',
    }),
    { attachTo: document.body },
  )
  return { wrapper, value }
}

describe('ScriptArgumentInput (docs/connectors-plan.md §13.5)', () => {
  it('takes text as text, and a multi-line text in an area', async () => {
    const { wrapper, value } = mount(parameter({ name: 'Site' }))
    await wrapper.get('input#arg').setValue('shop-de')
    expect(value.value).toBe('shop-de')
    const area = mount(parameter({ name: 'Note' }), undefined, true)
    await area.wrapper.get('textarea#arg').setValue('one\ntwo')
    expect(area.value.value).toBe('one\ntwo')
  })

  it('shows a switch as a checkbox, a secret as a password and a number as a number field', () => {
    const has = (p: S['ScriptParameter'], selector: string) => mount(p).wrapper.element.outerHTML.includes(selector)
    expect(has(parameter({ isSwitch: true, type: 'SwitchParameter' }), 'type="checkbox"')).toBe(true)
    expect(has(parameter({ isSecure: true, type: 'SecureString' }), 'type="password"')).toBe(true)
    expect(has(parameter({ type: 'int', rangeMin: 1, rangeMax: 9 }), 'role="spinbutton"')).toBe(true)
    expect(has(parameter({ validateSet: ['Pages', 'All'] }), 'role="combobox"')).toBe(true)
  })
})
