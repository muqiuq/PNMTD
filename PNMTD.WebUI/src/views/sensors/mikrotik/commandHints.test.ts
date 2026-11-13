import { describe, expect, it } from 'vitest'
import { commandHints } from './commandHints'

const texts = (command: string) => commandHints(command).map((h) => h.text.split(':')[0])

describe('commandHints', () => {
  it('says nothing about an ordinary print', () => {
    expect(commandHints('/ip address print')).toEqual([])
    expect(commandHints('/log print where topics~"error"')).toEqual([])
  })

  it("warns when the text touches PNMT's objects", () => {
    expect(commandHints('/interface wireguard remove [find name=pnmt]')[0]).toMatchObject({ severity: 'warn' })
    expect(commandHints('/user remove PNMT')).toHaveLength(1)
    expect(commandHints('/system reset-configuration')[0]).toMatchObject({ severity: 'warn' })
  })

  it('points at commands that never end', () => {
    expect(texts('/ping 8.8.8.8')).toEqual(['Ping without count= runs until the timeout'])
    expect(texts('/ping 8.8.8.8 count=4')).toEqual([])
    expect(texts('/tool traceroute 8.8.8.8')).toHaveLength(1)
    expect(texts('/interface monitor-traffic ether1')).toHaveLength(1)
    expect(texts('/interface monitor-traffic ether1 once')).toEqual([])
    expect(texts('/tool torch interface=ether1')).toHaveLength(1)
    expect(texts('/tool torch interface=ether1 duration=5s')).toEqual([])
  })

  it('suggests :execute for a reboot', () => {
    expect(texts('/system reboot')).toHaveLength(1)
    expect(texts(':execute {/system reboot}')).toEqual([])
  })
})
