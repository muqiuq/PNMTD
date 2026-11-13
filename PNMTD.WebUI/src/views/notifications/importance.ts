import type { S } from '@/api/resources'

/** Low, Normal or High (docs/ai-operations-plan.md §11.1). */
export type Importance = S['Importance']

export const importances: Importance[] = ['High', 'Normal', 'Low']

export const importanceOptions = importances.map((value) => ({ label: value, value }))

/** A sensor's choice: its own, or its host's ("host"). */
export const HOST = 'host'

export function sensorImportanceOptions(hostImportance: Importance | null | undefined) {
  return [{ label: `As its host (${hostImportance ?? 'Normal'})`, value: HOST }, ...importanceOptions]
}

/** The Tag severity of an importance: High stands out, Low recedes. */
export function importanceSeverity(importance: Importance | null | undefined): 'danger' | 'secondary' | 'info' {
  return importance === 'High' ? 'danger' : importance === 'Low' ? 'secondary' : 'info'
}
