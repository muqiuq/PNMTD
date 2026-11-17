import { useQueryClient } from '@tanstack/vue-query'
import { keys, type S } from '@/api/resources'

/**
 * A change of an Advanced sensor's rules or fields answers with its new detail (docs/advanced-sensor-plan.md §7.5): it goes
 * into the sensor's cached page at once, and its reports, paths and charts are fetched again.
 */
export function useApplyAdvanced() {
  const queryClient = useQueryClient()
  return async (sensorId: string, detail: S['AdvancedSensorDetail'] | undefined | null) => {
    if (detail) {
      queryClient.setQueryData<S['SensorDetail']>(keys.sensor(sensorId), (old) =>
        old ? { ...old, advanced: detail } : old,
      )
    }
    await queryClient.invalidateQueries({ queryKey: keys.advanced(sensorId) })
    await queryClient.invalidateQueries({ queryKey: keys.sensor(sensorId), exact: true })
  }
}
