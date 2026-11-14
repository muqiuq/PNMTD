using System.Text;

namespace PNMTD.Sensor.Windows.Scripts;

/// <summary>
/// An example script (docs/windows-sensor-plan.md §7.1), written once to Documents\PNMT Sensor and opened in the editor: it follows
/// the best practices PNMT's parser checks, only reads, and ends with exit code 1 when a disk is low.
/// </summary>
internal static class ExampleScript
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PNMT Sensor");

    public static string ExampleFile => Path.Combine(Folder, "Get-DiskReport.ps1");

    /// <summary>Writes the example unless it is there already (it may have been edited), and answers its path.</summary>
    public static string Write()
    {
        if (!File.Exists(ExampleFile))
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(ExampleFile, Text.Replace("\n", "\r\n"), new UTF8Encoding(true));
        }
        return ExampleFile;
    }

    public const string Text = """
        <#
        .SYNOPSIS
            Reports the disks' free space, and the automatic services that don't run.
        .DESCRIPTION
            An example for PNMT Sensor. It only reads and changes nothing. It ends with exit code 1 when a disk
            has less free space than MinFreePercent, so PNMT's history and a scheduled task's last result show it.

            Register it in the PNMT Sensor window (Scripts, Add script…), with a schedule if it should run by
            itself. To run it from PNMT too, add the same text to PNMT's script library (Admin, Windows servers,
            Scripts): the SHA-256 must match.
        .PARAMETER MinFreePercent
            A disk with less free space than this, in percent, is reported as low. 10 by default.
        .PARAMETER IncludeServices
            Also list the automatic services that are stopped.
        .EXAMPLE
            .\Get-DiskReport.ps1 -MinFreePercent 15 -IncludeServices
        #>
        [CmdletBinding()]
        param(
            [Parameter(HelpMessage = 'Free space below this, in percent, is low')]
            [ValidateRange(1, 99)]
            [int] $MinFreePercent = 10,

            [switch] $IncludeServices
        )
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'

        $low = 0
        foreach ($disk in Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType = 3') {
            $free = if ($disk.Size) { [math]::Round(100 * $disk.FreeSpace / $disk.Size, 1) } else { 0 }
            $state = 'ok'
            if ($free -lt $MinFreePercent) {
                $low++
                $state = 'LOW'
            }
            '{0}  {1,6:N1} % free of {2,8:N1} GB  {3}' -f $disk.DeviceID, $free, ($disk.Size / 1GB), $state
        }

        if ($IncludeServices) {
            ''
            'Automatic services that are stopped:'
            Get-CimInstance -ClassName Win32_Service -Filter "StartMode = 'Auto' AND State <> 'Running'" |
                Sort-Object -Property DisplayName |
                ForEach-Object { '  {0} ({1})' -f $_.DisplayName, $_.Name }
        }

        if ($low -gt 0) {
            Write-Warning "$low disk(s) below $MinFreePercent % free."
            exit 1
        }
        """;
}
