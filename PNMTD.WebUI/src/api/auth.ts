import { call, client, type Schemas } from './client'

export type CurrentUser = Schemas['CurrentUser']
export type SessionFlags = Schemas['SessionFlags']
export type SessionTimes = Schemas['SessionTimes']
export type LoginResponse = Schemas['LoginResponse']
export type TotpSetup = Schemas['TotpSetup']
export type SystemInfo = Schemas['SystemInfo']

/** Sign-in, the session and the own account (docs/webui-migration-plan.md §4.5, §4.9). */
export const authApi = {
  info: () => call(client.GET('/api/v1/system/info')),
  me: () => call(client.GET('/api/v1/auth/me')),
  login: (username: string, password: string) =>
    call(client.POST('/api/v1/auth/login', { body: { username, password } })),
  loginTotp: (code: string) => call(client.POST('/api/v1/auth/login/totp', { body: { code } })),
  /** The day's 2FA code (docs/mcp-plan.md §3.3): the session goes on. */
  totpDaily: (code: string) => call(client.POST('/api/v1/auth/totp/daily', { body: { code } })),
  logout: () => call(client.POST('/api/v1/auth/logout')),
  changePassword: (currentPassword: string, newPassword: string) =>
    call(client.POST('/api/v1/account/password', { body: { currentPassword, newPassword } })),
  revokeSessions: () => call(client.POST('/api/v1/account/sessions/revoke')),
  totpSetup: () => call(client.POST('/api/v1/account/totp/setup')),
  totpConfirm: (code: string) => call(client.POST('/api/v1/account/totp/confirm', { body: { code } })),
  totpDisable: (password: string, code: string) =>
    call(client.POST('/api/v1/account/totp/disable', { body: { password, code } })),
}
