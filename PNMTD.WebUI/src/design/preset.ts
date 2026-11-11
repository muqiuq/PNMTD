import { definePreset, palette } from '@primeuix/themes'
import Aura from '@primeuix/themes/aura'

/** The blue of the PNMT logo (docs/logos). */
export const brandColor = '#3b59a5'

/** The class on <html> that switches PrimeVue and base.css to dark (stores/preferences.ts). */
export const darkClass = 'pnmt-dark'

/**
 * PrimeVue's Aura with PNMT's colours and density (docs/webui-migration-plan.md §5.3). The root font
 * size is 14 px (base.css), so every rem is smaller than Aura's 16 px; paddings are trimmed further
 * so that inputs and table rows come out at about 32 px.
 */
export const PnmtPreset = definePreset(Aura, {
  semantic: {
    primary: palette(brandColor),
    formField: {
      paddingX: '0.625rem',
      paddingY: '0.4375rem',
      sm: { fontSize: '0.875rem', paddingX: '0.5rem', paddingY: '0.25rem' },
    },
    list: {
      option: { padding: '0.375rem 0.625rem' },
    },
    navigation: {
      item: { padding: '0.4375rem 0.625rem' },
    },
    colorScheme: {
      light: {
        surface: {
          0: '#ffffff',
          50: '{slate.50}',
          100: '{slate.100}',
          200: '{slate.200}',
          300: '{slate.300}',
          400: '{slate.400}',
          500: '{slate.500}',
          600: '{slate.600}',
          700: '{slate.700}',
          800: '{slate.800}',
          900: '{slate.900}',
          950: '{slate.950}',
        },
        primary: {
          color: '{primary.500}',
          contrastColor: '#ffffff',
          hoverColor: '{primary.600}',
          activeColor: '{primary.700}',
        },
      },
      dark: {
        surface: {
          0: '#ffffff',
          50: '{zinc.50}',
          100: '{zinc.100}',
          200: '{zinc.200}',
          300: '{zinc.300}',
          400: '{zinc.400}',
          500: '{zinc.500}',
          600: '{zinc.600}',
          700: '{zinc.700}',
          800: '{zinc.800}',
          900: '{zinc.900}',
          950: '{zinc.950}',
        },
        primary: {
          color: '{primary.300}',
          contrastColor: '{zinc.950}',
          hoverColor: '{primary.200}',
          activeColor: '{primary.100}',
        },
      },
    },
  },
  components: {
    datatable: {
      headerCell: { padding: '0.375rem 0.625rem' },
      bodyCell: { padding: '0.3125rem 0.625rem' },
    },
    card: {
      body: { padding: '0.875rem', gap: '0.5rem' },
    },
  },
})
