import pluginVue from 'eslint-plugin-vue'
import { defineConfigWithVueTs, vueTsConfigs } from '@vue/eslint-config-typescript'
import skipFormatting from '@vue/eslint-config-prettier/skip-formatting'

// Formatting is Prettier's (npm run format:check); ESLint checks the code.
export default defineConfigWithVueTs(
  {
    ignores: [
      'dist/**',
      'dist-board/**',
      'node_modules/**',
      'src/api/schema.d.ts',
      'test-results/**',
      'playwright-report/**',
    ],
  },
  pluginVue.configs['flat/recommended'],
  vueTsConfigs.recommended,
  skipFormatting,
  {
    rules: {
      'vue/multi-word-component-names': 'off',
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
    },
  },
  // The admin UI may draw a board (the preview), never import the board app (docs/boards-plan.md §4.3).
  {
    files: ['src/**/*.ts', 'src/**/*.vue'],
    ignores: ['src/board-app/**'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: ['@/board-app', '@/board-app/*'],
              message: 'The admin UI never imports the board app (docs/boards-plan.md §4.3).',
            },
          ],
        },
      ],
    },
  },
  // What a board is drawn with stays apart from the admin UI (§4.3): only the API's types and pure helpers, so that the board app works
  // where only /pnmt-board/ is reachable, and its bundle stays small. src/shared holds what both use.
  {
    files: [
      'src/boards/**/*.ts',
      'src/boards/**/*.vue',
      'src/board-app/**/*.ts',
      'src/board-app/**/*.vue',
      'src/shared/**/*.ts',
    ],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: ['@/api/*', '!@/api/schema'],
              message: "Only the API's types (@/api/schema): the board app has a client of its own.",
            },
            {
              group: [
                '@/stores',
                '@/stores/*',
                '@/router',
                '@/router/*',
                '@/layouts/*',
                '@/views',
                '@/views/*',
                '@/composables/*',
              ],
              message: "The admin UI's session, router and views stay out of the board app (docs/boards-plan.md §4.3).",
            },
            {
              group: [
                'primevue',
                'primevue/*',
                '@primeuix/*',
                '@tanstack/*',
                'pinia',
                'vue-router',
                'monaco-editor',
                'mermaid',
                '@milkdown/*',
                'highlight.js',
                'highlight.js/*',
              ],
              message:
                "Plain HTML and CSS on the board's variables: themes control everything, and the bundle stays small (§4.3).",
            },
            {
              group: ['@/board-app', '@/board-app/*'],
              message: 'What both apps draw (src/boards) never imports the board app.',
            },
          ],
        },
      ],
    },
  },
)
