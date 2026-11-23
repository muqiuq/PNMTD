import { createApp } from 'vue'
import '@fontsource-variable/inter'
import '@fontsource-variable/jetbrains-mono'
import './board-app.css'
import BoardApp from './BoardApp.vue'

// The board app (docs/boards-plan.md §4.3): no router, no Pinia, no VueQuery, no PrimeVue; only the board.
createApp(BoardApp).mount('#board')
