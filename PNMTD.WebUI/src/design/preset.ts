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
 *
 * The light scheme has more contrast than Aura's: its text (slate 700), muted text (500), borders
 * (200) and 500-shade text on tints were hard to read at 12–14 px. Text is slate 900, muted text 700,
 * icons 600, borders a step darker, coloured text on tints 700–800, filled buttons 600. Dark is Aura's with a
 * little more contrast: muted text, icons and labels zinc 300, borders a step brighter.
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
        text: {
          color: '{surface.900}',
          hoverColor: '{surface.950}',
          mutedColor: '{surface.700}',
          hoverMutedColor: '{surface.800}',
        },
        content: { borderColor: '{surface.300}' },
        formField: {
          borderColor: '{surface.400}',
          hoverBorderColor: '{surface.500}',
          color: '{surface.900}',
          floatLabelColor: '{surface.600}',
          floatLabelActiveColor: '{surface.600}',
          iconColor: '{surface.600}',
        },
        overlay: {
          select: { borderColor: '{surface.300}' },
          popover: { borderColor: '{surface.300}' },
          modal: { borderColor: '{surface.300}' },
        },
        list: {
          option: { icon: { color: '{surface.600}', focusColor: '{surface.800}' } },
        },
        navigation: {
          item: { icon: { color: '{surface.600}', focusColor: '{surface.800}', activeColor: '{surface.800}' } },
          submenuIcon: { color: '{surface.600}', focusColor: '{surface.800}', activeColor: '{surface.800}' },
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
        // A little more contrast than Aura's dark: muted text, icons, labels and borders one step brighter.
        text: { mutedColor: '{surface.300}', hoverMutedColor: '{surface.200}' },
        content: { borderColor: '{surface.600}' },
        formField: {
          borderColor: '{surface.500}',
          hoverBorderColor: '{surface.400}',
          floatLabelColor: '{surface.300}',
          floatLabelActiveColor: '{surface.300}',
          iconColor: '{surface.300}',
        },
        overlay: {
          select: { borderColor: '{surface.600}' },
          popover: { borderColor: '{surface.600}' },
          modal: { borderColor: '{surface.600}' },
        },
        list: {
          option: { icon: { color: '{surface.400}', focusColor: '{surface.300}' } },
        },
        navigation: {
          item: { icon: { color: '{surface.400}', focusColor: '{surface.300}', activeColor: '{surface.300}' } },
          submenuIcon: { color: '{surface.400}', focusColor: '{surface.300}', activeColor: '{surface.300}' },
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
    button: {
      colorScheme: {
        light: {
          root: {
            secondary: {
              background: '{surface.200}',
              hoverBackground: '{surface.300}',
              activeBackground: '{surface.400}',
              borderColor: '{surface.200}',
              hoverBorderColor: '{surface.300}',
              activeBorderColor: '{surface.400}',
              color: '{surface.800}',
              hoverColor: '{surface.900}',
              activeColor: '{surface.950}',
            },
            success: filled('green'),
            info: filled('sky'),
            warn: filled('orange'),
            help: filled('purple'),
            danger: filled('red'),
          },
          outlined: {
            primary: { borderColor: '{primary.300}' },
            secondary: { borderColor: '{surface.400}', color: '{surface.700}' },
            success: { borderColor: '{green.300}', color: '{green.700}' },
            info: { borderColor: '{sky.300}', color: '{sky.700}' },
            warn: { borderColor: '{orange.300}', color: '{orange.700}' },
            help: { borderColor: '{purple.300}', color: '{purple.700}' },
            danger: { borderColor: '{red.300}', color: '{red.700}' },
          },
          text: {
            secondary: { color: '{surface.700}' },
            success: { color: '{green.700}' },
            info: { color: '{sky.700}' },
            warn: { color: '{orange.700}' },
            help: { color: '{purple.700}' },
            danger: { color: '{red.700}' },
          },
        },
      },
    },
    tag: {
      colorScheme: {
        light: {
          primary: { color: '{primary.800}' },
          secondary: { background: '{surface.200}', color: '{surface.800}' },
          success: { color: '{green.800}' },
          info: { color: '{sky.800}' },
          warn: { color: '{orange.800}' },
          danger: { color: '{red.800}' },
        },
      },
    },
    message: {
      colorScheme: {
        light: {
          info: tinted('blue'),
          success: tinted('green'),
          warn: tinted('yellow'),
          error: tinted('red'),
          secondary: {
            borderColor: '{surface.300}',
            color: '{surface.700}',
            outlined: { color: '{surface.700}', borderColor: '{surface.700}' },
            simple: { color: '{surface.700}' },
          },
        },
      },
    },
    togglebutton: {
      colorScheme: {
        light: {
          root: {
            background: '{surface.200}',
            checkedBackground: '{surface.200}',
            hoverBackground: '{surface.200}',
            borderColor: '{surface.200}',
            checkedBorderColor: '{surface.200}',
            color: '{surface.700}',
            hoverColor: '{surface.900}',
          },
          icon: { color: '{surface.700}', hoverColor: '{surface.900}' },
        },
      },
    },
    toggleswitch: {
      colorScheme: {
        light: {
          root: { background: '{surface.400}', hoverBackground: '{surface.500}' },
        },
      },
    },
  },
})

/** A filled button in the light scheme: 600, darker on hover and press (Aura's 500 is too light under white text). */
function filled(colour: string) {
  return {
    background: `{${colour}.600}`,
    hoverBackground: `{${colour}.700}`,
    activeBackground: `{${colour}.800}`,
    borderColor: `{${colour}.600}`,
    hoverBorderColor: `{${colour}.700}`,
    activeBorderColor: `{${colour}.800}`,
  }
}

/** A message in the light scheme: 800 text on the 50 tint, a 300 border (Aura's 600 text was faint, yellow most). */
function tinted(colour: string) {
  return {
    borderColor: `{${colour}.300}`,
    color: `{${colour}.800}`,
    outlined: { color: `{${colour}.700}`, borderColor: `{${colour}.700}` },
    simple: { color: `{${colour}.700}` },
  }
}
