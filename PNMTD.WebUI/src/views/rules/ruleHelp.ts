/** How a recipient is read (§5.6): by its form, as the providers decide on the server. */
export const recipientHint = 'An e-mail address, pushover://<user key>, or an http(s):// URL for a webhook.'

export function providerIcon(provider: string): string {
  switch (provider) {
    case 'pushover':
      return 'pi pi-mobile'
    case 'http':
      return 'pi pi-link'
    case 'email':
      return 'pi pi-envelope'
    default:
      return 'pi pi-question-circle'
  }
}

export function providerLabel(provider: string): string {
  return provider === 'http'
    ? 'Webhook'
    : provider === 'email'
      ? 'E-mail'
      : provider === 'pushover'
        ? 'Pushover'
        : 'Unknown'
}
