import { api, keys } from '@/api/resources'
import { useActions } from '@/composables/useActions'

/**
 * A node's resets (docs/udp-iot-plan.md §3.7, §3.8), with the same confirmations on the sensor page
 * and the admin page. Both are audit-logged by the server.
 */
export function useNodeActions() {
  const { ask, done } = useActions()

  async function resetCounter(sensorId: string, name: string) {
    const ok = await ask(
      `Reset the counter of ${name}?`,
      'Its next packet sets a new counter, whatever its value. For a device that lost its counter and cannot JOIN; until that packet arrives, nothing is rejected as a replay.',
      'Reset counter',
    )
    return (
      ok &&
      (await done(() => api.udp.resetCounter(sensorId), {
        success: `Counter of ${name} reset`,
        invalidate: [keys.sensor(sensorId), keys.udp],
      }))
    )
  }

  async function resetSession(sensorId: string, name: string) {
    const ok = await ask(
      `Reset the session of ${name}?`,
      'The active and the pending session are dropped: with JOIN required, the device must JOIN again before its packets are accepted.',
      'Reset session',
    )
    return (
      ok &&
      (await done(() => api.udp.resetSession(sensorId), {
        success: `Session of ${name} reset`,
        invalidate: [keys.sensor(sensorId), keys.udp],
      }))
    )
  }

  return { resetCounter, resetSession }
}
