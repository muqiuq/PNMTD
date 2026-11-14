// Monaco's language definitions ship without types; monacoSetup.ts borrows JavaScript's tokens for JSON.
declare module 'monaco-editor/languages/definitions/javascript/javascript' {
  export const language: import('monaco-editor/editor/editor.api').languages.IMonarchLanguage
}
