using System.Diagnostics;
using System.Text;

namespace PNMTD.Sensor.Windows.Scripts;

/// <summary>
/// PowerShell for scripts (docs/windows-sensor-plan.md §7): the fixed parser (Windows PowerShell 5.1's own parser, which never
/// runs the script), the wrapper that splats the arguments (never pasted into a command line), and the runner that starts a
/// script as another account through a scheduled task.
/// </summary>
internal static class PowerShell
{
    public static string Executable(string shell) => shell == "pwsh"
        ? FindPwsh() ?? throw new InvalidOperationException("pwsh (PowerShell 7) is not installed on this server.")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string? FindPwsh()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Runs a PowerShell file with arguments (each its own argv entry) and stdin; answers stdout, stderr and the exit code.</summary>
    public static async Task<(int ExitCode, string Output, string Errors)> RunFileAsync(string shell, string file, IReadOnlyList<string> arguments, string? input,
        TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Executable(shell))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
        }
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"PowerShell did not finish within {timeout.TotalSeconds:0} s.");
        }
        return (process.ExitCode, await output, await errors);
    }

    /// <summary>
    /// The parser (§7.1): the parameters (type, mandatory, default, position, ValidateSet, ValidateRange, HelpMessage), the
    /// comment-based help, the syntax errors and the best-practice findings, as JSON. It reads the script from a file; nothing in
    /// it is run.
    /// </summary>
    public const string Parser = """
        param([string]$Path)
        $ErrorActionPreference = 'Stop'
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
        function Read-Parameters($parameters) {
          $list = @()
          foreach ($p in @($parameters)) {
            if ($null -eq $p) { continue }
            $type = if ($p.StaticType -and $p.StaticType.Name -ne 'Object') { $p.StaticType.Name } else { 'Object' }
            $mandatory = $false; $position = $null; $help = $null; $set = $null; $min = $null; $max = $null
            foreach ($a in $p.Attributes) {
              if ($a -isnot [System.Management.Automation.Language.AttributeAst]) { continue }
              switch ($a.TypeName.Name) {
                'Parameter' {
                  foreach ($n in $a.NamedArguments) {
                    switch ($n.ArgumentName) {
                      'Mandatory' { $mandatory = if ($n.ExpressionOmitted) { $true } else { [bool]$n.Argument.SafeGetValue() } }
                      'Position' { $position = [int]$n.Argument.SafeGetValue() }
                      'HelpMessage' { $help = [string]$n.Argument.SafeGetValue() }
                    }
                  }
                }
                'ValidateSet' { $set = @($a.PositionalArguments | ForEach-Object { [string]$_.SafeGetValue() }) }
                'ValidateRange' {
                  if ($a.PositionalArguments.Count -ge 2) {
                    $min = [double]$a.PositionalArguments[0].SafeGetValue(); $max = [double]$a.PositionalArguments[1].SafeGetValue()
                  }
                }
              }
            }
            $default = if ($p.DefaultValue) { $p.DefaultValue.Extent.Text } else { $null }
            $list += [ordered]@{
              name = $p.Name.VariablePath.UserPath; type = $type; mandatory = $mandatory; default = $default; position = $position
              validateSet = $set; rangeMin = $min; rangeMax = $max; helpMessage = $help
              isSwitch = ($type -eq 'SwitchParameter'); isSecure = ($type -eq 'SecureString')
            }
          }
          ,$list
        }
        function Read-Help($content) {
          if (-not $content) { return $null }
          $described = @{}
          foreach ($key in $content.Parameters.Keys) { $described[$key] = $content.Parameters[$key] }
          [ordered]@{ synopsis = $content.Synopsis; description = $content.Description; parameters = $described }
        }
        $params = if ($ast.ParamBlock) { Read-Parameters $ast.ParamBlock.Parameters } else { @() }
        $content = $ast.GetHelpContent()
        $help = Read-Help $content
        # docs/connectors-plan.md §8.1: the functions defined at the top level (not inside other functions), each with its own
        # parameters, read by the same code as the script's, and its help.
        $functions = @()
        foreach ($f in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
          $own = if ($f.Body.ParamBlock) { Read-Parameters $f.Body.ParamBlock.Parameters } elseif ($f.Parameters) { Read-Parameters $f.Parameters } else { @() }
          $functions += [ordered]@{ name = $f.Name; line = $f.Extent.StartLineNumber; parameters = @($own); help = (Read-Help $f.GetHelpContent()) }
        }
        $findings = @()
        $binding = $ast.ParamBlock -and ($ast.ParamBlock.Attributes | Where-Object { $_.TypeName.Name -eq 'CmdletBinding' })
        if (-not $binding) { $findings += [ordered]@{ rule = 'CmdletBinding'; message = 'No [CmdletBinding()]: the script takes neither -Verbose nor -ErrorAction.' } }
        foreach ($p in $params) {
          if ($p.type -eq 'Object') { $findings += [ordered]@{ rule = 'TypedParameters'; message = "The parameter `$$($p.name) has no type." } }
          if ($p.name -match 'Password|Secret|Credential' -and $p.type -notin @('SecureString', 'PSCredential')) {
            $findings += [ordered]@{ rule = 'PlainTextPassword'; message = "`$$($p.name) looks like a password but is a $($p.type): use [securestring] or a credential." }
          }
        }
        if (-not $content) { $findings += [ordered]@{ rule = 'Help'; message = 'No comment-based help (.SYNOPSIS, .DESCRIPTION, .PARAMETER).' } }
        $iex = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -in @('Invoke-Expression', 'iex') }, $true)
        foreach ($n in $iex) { $findings += [ordered]@{ rule = 'InvokeExpression'; message = 'Invoke-Expression runs strings as code.'; line = $n.Extent.StartLineNumber } }
        $text = $ast.Extent.Text
        if ($text -notmatch 'Set-StrictMode') { $findings += [ordered]@{ rule = 'StrictMode'; message = 'No Set-StrictMode.' } }
        if ($text -notmatch '\$ErrorActionPreference\s*=\s*[''"]Stop') { $findings += [ordered]@{ rule = 'ErrorAction'; message = "No `$ErrorActionPreference = 'Stop'." } }
        if ($functions.Count -gt 0) {
          # Calling a function dot-sources the script: its top level runs first.
          foreach ($block in @($ast.BeginBlock, $ast.ProcessBlock, $ast.EndBlock)) {
            if (-not $block) { continue }
            foreach ($statement in $block.Statements) {
              if ($statement -is [System.Management.Automation.Language.FunctionDefinitionAst]) { continue }
              $line = $statement.Extent.Text
              if ($line -match '^\s*Set-StrictMode\b') { continue }
              if ($statement -is [System.Management.Automation.Language.AssignmentStatementAst] -and $statement.Left.Extent.Text -match '^\$ErrorActionPreference$') { continue }
              if ($statement -is [System.Management.Automation.Language.IfStatementAst] -and $line -match '\$MyInvocation\.InvocationName') { continue }
              $findings += [ordered]@{ rule = 'function-top-level-code'; message = "Calling a function runs this line first (line $($statement.Extent.StartLineNumber))."; line = $statement.Extent.StartLineNumber }
            }
          }
          if (@($params | Where-Object { $_.mandatory }).Count -gt 0) {
            $findings += [ordered]@{ rule = 'function-script-mandatory'; message = "The script's own param() has a mandatory parameter: dot-sourcing would ask for it, so its functions can't be called." }
          }
        }
        $errs = @($errors | ForEach-Object { [ordered]@{ message = $_.Message; line = $_.Extent.StartLineNumber; column = $_.Extent.StartColumnNumber } })
        $json = [ordered]@{ parameters = @($params); help = $help; errors = $errs; findings = @($findings); functions = @($functions) } | ConvertTo-Json -Depth 10 -Compress
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
        """;

    /// <summary>
    /// The wrapper (§7.3): reads the arguments from args.json and the secure ones from secure.json (deleted at once), splats them
    /// into the script (or, with function.txt, into that function of it), and writes the output and the exit code next to it.
    /// Arguments never touch a command line.
    /// </summary>
    public const string Wrapper = """
        param([string]$Folder)
        $ErrorActionPreference = 'Stop'
        $splat = @{}
        $arguments = Get-Content -LiteralPath (Join-Path $Folder 'args.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($arguments) { foreach ($p in $arguments.PSObject.Properties) { $splat[$p.Name] = $p.Value } }
        $secureFile = Join-Path $Folder 'secure.json'
        if (Test-Path -LiteralPath $secureFile) {
          $secure = Get-Content -LiteralPath $secureFile -Raw -Encoding UTF8 | ConvertFrom-Json
          Remove-Item -LiteralPath $secureFile -Force
          foreach ($p in $secure.PSObject.Properties) { $splat[$p.Name] = ConvertTo-SecureString -String $p.Value -AsPlainText -Force }
        }
        $output = Join-Path $Folder 'output.txt'
        $code = 0
        try {
          # docs/connectors-plan.md §8.1: a function's name comes in a file, never on a command line; dot-sourcing defines the script's
          # functions here, and Get-Command -CommandType Function resolves the name to a function only, never an alias or a cmdlet.
          $functionFile = Join-Path $Folder 'function.txt'
          if (Test-Path -LiteralPath $functionFile) {
            $name = (Get-Content -LiteralPath $functionFile -Raw -Encoding UTF8).Trim()
            . (Join-Path $Folder 'script.ps1')
            $command = Get-Command -Name $name -CommandType Function -ErrorAction Stop
            & $command @splat *>&1 | Out-File -LiteralPath $output -Encoding utf8 -Width 4096
          } else {
            & (Join-Path $Folder 'script.ps1') @splat *>&1 | Out-File -LiteralPath $output -Encoding utf8 -Width 4096
          }
          if ($LASTEXITCODE) { $code = $LASTEXITCODE }
        } catch {
          $_ | Out-String -Width 4096 | Out-File -LiteralPath $output -Encoding utf8 -Append
          $code = 1
        }
        Set-Content -LiteralPath (Join-Path $Folder 'exitcode.txt') -Value $code
        exit $code
        """;

    /// <summary>
    /// Another account (§7.4): a scheduled task with that principal runs the wrapper, as LogonUser and CreateProcessAsUser would,
    /// without either (CreateProcessWithLogonW refuses LocalSystem). The account and its password come on stdin, the run folder
    /// is opened to the account, and the task is removed at the end. A gMSA needs no password.
    /// </summary>
    public const string TaskRunner = """
        $ErrorActionPreference = 'Stop'
        $in = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::In.ReadToEnd().Trim())) | ConvertFrom-Json
        & icacls.exe $in.folder /grant "$($in.account):(OI)(CI)M" | Out-Null
        $arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$($in.wrapper)`" -Folder `"$($in.folder)`""
        $action = New-ScheduledTaskAction -Execute $in.shell -Argument $arguments
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds $in.timeout) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        $name = "PNMT Sensor run $($in.runId)"
        if ($in.kind -eq 'Gmsa') {
          $principal = New-ScheduledTaskPrincipal -UserId $in.account -LogonType Password -RunLevel Highest
          Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings | Out-Null
        } else {
          Register-ScheduledTask -TaskName $name -Action $action -Settings $settings -User $in.account -Password $in.password -RunLevel Highest | Out-Null
        }
        $in.password = $null
        try {
          Start-ScheduledTask -TaskName $name
          $deadline = (Get-Date).AddSeconds([int]$in.timeout + 30)
          do {
            Start-Sleep -Milliseconds 500
            $state = (Get-ScheduledTask -TaskName $name).State
          } while ((($state -eq 'Running') -or ($state -eq 'Queued')) -and ((Get-Date) -lt $deadline))
          Write-Output (Get-ScheduledTaskInfo -TaskName $name).LastTaskResult
        } finally {
          Unregister-ScheduledTask -TaskName $name -Confirm:$false
        }
        """;
}
