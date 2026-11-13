/**
 * A router's scheduled backup (docs/mikrotik-backup-plan.md §2): off (null), or an export every so many hours,
 * up to a year. Undefined is a custom interval not filled in yet.
 */
export type BackupSchedule = number | null | undefined

export const MAX_BACKUP_HOURS = 8760

export const SCHEDULE_PRESETS: { label: string; hours: number | null }[] = [
  { label: 'Off', hours: null },
  { label: 'Every 6 hours', hours: 6 },
  { label: 'Daily', hours: 24 },
  { label: 'Weekly', hours: 168 },
  { label: 'Monthly (30 days)', hours: 720 },
]

/** "Off", "Daily", "Every 3 days", "Every 12 hours", …: for lists and the status strip. */
export function scheduleLabel(hours: BackupSchedule): string {
  if (hours === null) return 'Off'
  if (hours === undefined) return '–'
  if (hours % 24 === 0) {
    const days = hours / 24
    if (days === 1) return 'Daily'
    if (days === 7) return 'Weekly'
    if (days === 30) return 'Monthly'
    if (days === 365) return 'Yearly'
    return `Every ${days} days`
  }
  return hours === 1 ? 'Hourly' : `Every ${hours} hours`
}

export function scheduleError(hours: BackupSchedule): string | undefined {
  if (hours === null) return undefined
  return hours === undefined || !Number.isInteger(hours) || hours < 1 || hours > MAX_BACKUP_HOURS
    ? 'Off, or from 1 hour to 365 days.'
    : undefined
}

/** When the next scheduled export is due: null when off; now when there was none yet. */
export function nextBackupAt(
  lastExportAt: string | null | undefined,
  hours: BackupSchedule,
  now = new Date(),
): Date | null {
  if (hours === null || hours === undefined) return null
  if (!lastExportAt) return now
  return new Date(new Date(lastExportAt).getTime() + hours * 3_600_000)
}
