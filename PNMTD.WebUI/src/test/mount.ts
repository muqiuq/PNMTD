import { mount, type ComponentMountingOptions } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import type { Component } from 'vue'

/** A component with what main.ts installs: PrimeVue (unstyled here), toasts, the tooltip directive. */
export function mountWithUi<C extends Component>(component: C, options: ComponentMountingOptions<C> = {}) {
  return mount(component, {
    ...options,
    global: {
      plugins: [[PrimeVue, { unstyled: true }], ToastService],
      directives: { tooltip: Tooltip },
      stubs: { RouterLink: { template: '<a><slot /></a>' } },
      ...(options.global ?? {}),
    },
  } as ComponentMountingOptions<C>)
}
