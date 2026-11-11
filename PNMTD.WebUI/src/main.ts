import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import Tooltip from 'primevue/tooltip'
import '@fontsource-variable/inter'
import '@fontsource-variable/jetbrains-mono'
import 'primeicons/primeicons.css'
import './design/base.css'
import { PnmtPreset, darkClass } from './design/preset'
import { queryClient } from './api/queries'
import App from './App.vue'
import { router } from './router'
import { installSessionHandling } from './composables/sessionHandling'

const app = createApp(App)
app.use(createPinia())
app.use(router)
app.use(VueQueryPlugin, { queryClient })
app.use(PrimeVue, {
  theme: { preset: PnmtPreset, options: { darkModeSelector: `.${darkClass}`, cssLayer: false } },
  ripple: false,
})
app.use(ToastService)
app.use(ConfirmationService)
app.directive('tooltip', Tooltip)
installSessionHandling(router)
app.mount('#app')
