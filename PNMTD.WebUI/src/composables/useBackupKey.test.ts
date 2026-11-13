import { afterEach, describe, expect, it } from 'vitest'
import {
  clearBackupKey,
  decryptBackup,
  fromBase64,
  gunzip,
  loadBackupKey,
  newBackupKey,
  recipientOf,
  secretKeyIn,
  useBackupKey,
} from './useBackupKey'

// The demo and test key (e2e/fixtures/backup-test.age-key), and an export PNMTD's own encryption wrote with its
// public key (PNMTD.Tests AgeEncryptionTests.An_export_for_the_browsers_test): the browser reads what the server wrote.
const KEY_FILE = `# public key: age1d35svtrde4pf0x47gje95qx9yzzm6cqm8jtr8f95ye4xzye2n3nqglc96t
AGE-SECRET-KEY-1T3QXPSTXSMJ5CL3W263Q9066GCFT5G3HN47A2AVJMC0RQVW7Q0ZQU3HV2W
`
const ENCRYPTED =
  'YWdlLWVuY3J5cHRpb24ub3JnL3YxCi0+IFgyNTUxOSBkQjRSVlFOR0JBaUsrOFBMa1llUCt3SUttd3dqUTNEemU4ZW9BeWxUWXcwClB1NkJDU0xzaUR1MlhUdElBdm9lcEdYa1ZzNVN1cExrSktIQnBoUXdBNkkKLS0tIEI5ZDdrVW1zWC94bmdsZlpGMmpjMjhML1hxNkVOS3BORVpRMElSRzNmMUEKInlXiJnNLgzfg9/6pAioyrvKfNG/82yH4iDYp3Hbk7Pu76PA1Dl2zeHhhORugUjK5QyuS5a+ZjMuJfE/3zE8qp07ru4Uj0oRZoXMJ0IU1J7LMCVkN1WwXYfEBmQTKfA2uvJoGhf7HHmeviRiVkEls8Loaw=='

afterEach(clearBackupKey)

describe('the backup key', () => {
  it('finds the secret key in a key file', () => {
    expect(secretKeyIn(KEY_FILE)).toMatch(/^AGE-SECRET-KEY-1/)
    expect(secretKeyIn('age1d35svtrde4pf0x47gje95qx9yzzm6cqm8jtr8f95ye4xzye2n3nqglc96t')).toBeNull()
  })

  it('decrypts an export PNMTD encrypted', async () => {
    expect(await loadBackupKey(KEY_FILE)).toBe('age1d35svtrde4pf0x47gje95qx9yzzm6cqm8jtr8f95ye4xzye2n3nqglc96t')
    expect(useBackupKey().loaded.value).toBe(true)
    const text = await gunzip(await decryptBackup(fromBase64(ENCRYPTED)))
    expect(text).toBe('/ip dns set servers=1.1.1.1,8.8.8.8\n/system identity set name=st-gallen-gw\n')
    clearBackupKey()
    expect(useBackupKey().loaded.value).toBe(false)
    await expect(decryptBackup(fromBase64(ENCRYPTED))).rejects.toThrow()
  })

  it('makes a key file whose public key it names', async () => {
    const made = await newBackupKey()
    expect(made.recipient).toMatch(/^age1[0-9a-z]{58}$/)
    expect(made.file).toContain(`# public key: ${made.recipient}`)
    expect(await recipientOf(made.file)).toBe(made.recipient)
  })
})
