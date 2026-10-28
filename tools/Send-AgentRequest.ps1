<#
.SYNOPSIS
    Sends one request to the running Work to Unlock agent over its named pipe and prints the JSON response.
.EXAMPLE
    .\Send-AgentRequest.ps1 -Method getStatus
.EXAMPLE
    .\Send-AgentRequest.ps1 -Method saveRule -Payload '{"name":"Notepad","executablePath":"C:\\Windows\\System32\\notepad.exe","ruleType":"Work"}'
.EXAMPLE
    .\Send-AgentRequest.ps1 -Method getStatus -Instance demo   # an isolated instance started with WORKTOUNLOCK_INSTANCE=demo
#>
param(
    [Parameter(Mandatory)] [string]$Method,
    [string]$Payload,
    [string]$Instance = $env:WORKTOUNLOCK_INSTANCE,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$pipeName = "WorkToUnlock.Agent.$sid" + $(if ($Instance) { ".$Instance" } else { '' })

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(3000)
$writer = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
$reader = New-Object System.IO.StreamReader($pipe, (New-Object System.Text.UTF8Encoding($false)))
$request = '{"type":"request","id":1,"name":"' + $Method + '"' + $(if ($Payload) { ',"payload":' + $Payload } else { '' }) + '}'
$writer.WriteLine($request)
$writer.Flush()

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
while ([DateTime]::UtcNow -lt $deadline) {
    $line = $reader.ReadLine()
    if ($null -eq $line) { break }
    if ($line.Contains('"type":"response"')) { $line; break }
}
$pipe.Dispose()
