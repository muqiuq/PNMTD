import { ApiError } from '@/api/client'

/** What a failed sign-in step tells the user (§4.5): never which part was wrong. */
export function signInMessage(error: unknown): string {
  if (!(error instanceof ApiError)) {
    return 'Something went wrong. Try again.'
  }
  const minutes = error.retryAfter ? Math.max(1, Math.ceil(error.retryAfter / 60)) : null
  switch (error.code) {
    case 'invalid_credentials':
      return 'Wrong username or password.'
    case 'invalid_code':
      return 'Wrong code, or one that was used already. Wait for the next one and try again.'
    case 'locked_out':
      return `Too many failed sign-ins. Try again in ${minutes ?? 'a few'} minute${minutes === 1 ? '' : 's'}.`
    case 'rate_limited':
      return 'Too many attempts from this address. Wait a minute and try again.'
    case 'login_expired':
      return 'The sign-in has expired. Enter your password again.'
    case 'network':
      return 'The server cannot be reached.'
    default:
      return error.message
  }
}
