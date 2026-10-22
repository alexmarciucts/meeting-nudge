# MeetingNudge for Windows - installer / updater.
# Downloads the source, compiles it on this PC with Windows' built-in C# compiler (no SDK needed, and no
# "unknown publisher" warning because the app is built locally), adds a Start Menu shortcut and starts it.
#
#   irm https://raw.githubusercontent.com/alexmarciucts/meeting-nudge/main/windows/install.ps1 | iex

function Install-MeetingNudge {
    $ErrorActionPreference = 'Stop'
    $repoRaw = 'https://raw.githubusercontent.com/alexmarciucts/meeting-nudge/main/windows'
    $files = @('Calendar.cs', 'App.cs')
    $appDir = Join-Path $env:LOCALAPPDATA 'MeetingNudge'
    $exe = Join-Path $appDir 'MeetingNudge.exe'
    $buildOnly = [bool]$env:MEETINGNUDGE_BUILD_ONLY

    if ($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') {
        Write-Host "Your PC's security policy restricts PowerShell, so MeetingNudge can't be built here." -ForegroundColor Red
        return
    }

    New-Item -ItemType Directory -Force -Path $appDir | Out-Null

    # Source: next to this script when run from a checkout (or CI), otherwise from GitHub.
    $localDir = $null
    if ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot 'App.cs'))) { $localDir = $PSScriptRoot }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $sources = @()
    foreach ($f in $files) {
        if ($localDir) {
            $sources += [IO.File]::ReadAllText((Join-Path $localDir $f), [Text.Encoding]::UTF8)
        } else {
            Write-Host "Downloading $f..."
            $bytes = (New-Object Net.WebClient).DownloadData("$repoRaw/$($f)?nocache=$(Get-Random)")
            $sources += [Text.Encoding]::UTF8.GetString($bytes)
        }
    }

    Write-Host 'Building MeetingNudge...'
    $opts = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $opts.Add('CompilerVersion', 'v4.0')
    $provider = New-Object Microsoft.CSharp.CSharpCodeProvider($opts)
    $params = New-Object System.CodeDom.Compiler.CompilerParameters
    $params.GenerateExecutable = $true
    $params.GenerateInMemory = $false
    $tmpExe = Join-Path $env:TEMP ("MeetingNudge-" + [guid]::NewGuid().ToString('N') + '.exe')
    $params.OutputAssembly = $tmpExe
    $params.CompilerOptions = '/target:winexe /optimize+'
    foreach ($r in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')) {
        [void]$params.ReferencedAssemblies.Add($r)
    }
    $result = $provider.CompileAssemblyFromSource($params, [string[]]$sources)
    $errors = @($result.Errors | Where-Object { -not $_.IsWarning })
    if ($errors.Count -gt 0) {
        Write-Host 'Build failed:' -ForegroundColor Red
        $errors | ForEach-Object { Write-Host ("  line {0}: {1}" -f $_.Line, $_.ErrorText) -ForegroundColor Red }
        throw 'MeetingNudge build failed'
    }

    # Replace a running copy.
    Get-Process -Name MeetingNudge -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Move-Item -Force $tmpExe $exe

    if ($buildOnly) { Write-Host "Built $exe"; return }

    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'MeetingNudge.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $appDir
    $lnk.Description = 'Full-screen meeting reminders from Google Calendar'
    $lnk.Save()

    Start-Process $exe
    Write-Host ''
    Write-Host 'MeetingNudge is installed and running.' -ForegroundColor Green
    Write-Host 'Look for the red circle icon in the system tray (bottom right, maybe behind the ^ arrow).'
    Write-Host 'Tip: drag it next to the clock so it is always visible.'
}

Install-MeetingNudge
