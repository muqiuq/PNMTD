import type { S } from '@/api/resources'

/** Scripts' labels (docs/windows-sensor-plan.md §7). */

export const runStateSeverity = (s: S['ScriptRunState']) =>
  s === 'Done' ? 'success' : s === 'Running' || s === 'Sent' ? 'info' : s === 'Offline' ? 'secondary' : 'danger'

export const runAsLabel = (k: S['RunAsKind']) =>
  k === 'Service'
    ? 'the service (LocalSystem)'
    : k === 'Stored'
      ? 'stored on the server'
      : k === 'TypedPerRun'
        ? 'typed per run'
        : 'gMSA'

/** A library script's version, or a script registered on the server (version 0: it has no versions in PNMT). */
export const versionLabel = (n: number) => (n > 0 ? `v${n}` : 'registered on the server')

export const parseSeverity = (p: S['ScriptParseState']) =>
  p === 'Parsed' ? 'success' : p === 'Failed' ? 'danger' : 'warn'

/** A new script starts from a template that follows the best practices the parser checks (§7.1). */
export const template = `<#
.SYNOPSIS
    What the script does, in one line.
.DESCRIPTION
    What it does in detail, and what it changes on the server.
.PARAMETER Name
    What Name is for.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, HelpMessage = 'What Name is for')]
    [string] $Name
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Output "Hello, $Name, from $env:COMPUTERNAME"
`
