# Registers Glosa for this user: the "Open with" lists for the songs it plays, the apps under
# Settings > Apps > Default apps, and the Start menu. Run through install.cmd, from the
# unpacked folder, and again after moving that folder.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $root 'glosa.exe'
$ja = (Get-UICulture).TwoLetterISOLanguageName -eq 'ja'

function Say([string] $en, [string] $jp) { if ($ja) { Write-Host $jp } else { Write-Host $en } }

function Finish([int] $code) {
    Read-Host $(if ($ja) { 'Enter キーで閉じます' } else { 'Press Enter to close' }) | Out-Null
    exit $code
}

# Yes only for y or yes; anything else, or no answer at all, is no.
function Ask([string] $en, [string] $jp) {
    $answer = Read-Host $(if ($ja) { $jp } else { $en })
    return $answer -match '^\s*(y|yes)\s*$'
}

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Say "Glosa is not in $root." "Glosa が $root にありません。"
    Finish 1
}

Say @"
This registers Glosa ($exe) for this user:
  - a shortcut in the Start menu
  - "Open with" for MIDI and RCP files (.mid .midi .rmi .rcp .r36 .g18 .g36)
  - the list of apps in Settings > Apps > Default apps
The program stays in this folder. The app these files open with when double-clicked (the
default app) is not changed. Anything an earlier install registered is taken away first.
"@ @"
Glosa（$exe）を、このユーザーに次のとおり登録します。
  - スタートメニューのショートカット
  - MIDI・RCP ファイル（.mid .midi .rmi .rcp .r36 .g18 .g36）の「プログラムから開く」
  - 設定の「既定のアプリ」の一覧
プログラムはこのフォルダから動かしません。ダブルクリックでこれらのファイルを開くアプリ
（既定のアプリ）は変えません。
前に登録したものがあれば、先に外します。
"@
if (-not (Ask 'Register Glosa? [y/N]' '登録しますか？ [y/N]')) {
    Say 'Nothing was registered.' '何も登録しませんでした。'
    Finish 0
}

& (Join-Path $PSScriptRoot 'uninstall.ps1') -RegistrationsOnly

# What Glosa opens, as a program identifier per kind of file. The name shown for each is
# what Explorer calls the file once Glosa is its default.
$types = @(
    @{ Id = 'Glosa.MIDI'; En = 'MIDI Sequence'; Ja = 'MIDI シーケンス'; Extensions = '.mid', '.midi' }
    @{ Id = 'Glosa.RMI'; En = 'RIFF MIDI File'; Ja = 'RIFF MIDI ファイル'; Extensions = , '.rmi' }
    @{ Id = 'Glosa.RCP'; En = 'RCP Song'; Ja = 'レコンポーザの曲'; Extensions = '.rcp', '.r36', '.g18', '.g36' }
)

$command = "`"$exe`" `"%1`""
$icon = "`"$exe`",0"
$user = [Microsoft.Win32.Registry]::CurrentUser

function Set-Key([string] $path, [hashtable] $values) {
    $key = $user.CreateSubKey($path)
    try { foreach ($name in $values.Keys) { $key.SetValue($name, $values[$name]) } }
    finally { $key.Dispose() }
}

foreach ($type in $types) {
    $name = if ($ja) { $type.Ja } else { $type.En }
    Set-Key "Software\Classes\$($type.Id)" @{ '' = $name }
    Set-Key "Software\Classes\$($type.Id)\DefaultIcon" @{ '' = $icon }
    Set-Key "Software\Classes\$($type.Id)\shell\open\command" @{ '' = $command }
    foreach ($extension in $type.Extensions) {
        # A value, not the extension's default: this adds Glosa to the list and leaves
        # whatever opens the file now alone.
        Set-Key "Software\Classes\$extension\OpenWithProgids" @{ $type.Id = '' }
    }
}

# The program itself, which is what "Open with" and the Choose-an-app list show by name.
$supported = @{}
foreach ($type in $types) { foreach ($extension in $type.Extensions) { $supported[$extension] = '' } }
Set-Key 'Software\Classes\Applications\glosa.exe' @{ FriendlyAppName = 'Glosa' }
Set-Key 'Software\Classes\Applications\glosa.exe\DefaultIcon' @{ '' = $icon }
Set-Key 'Software\Classes\Applications\glosa.exe\shell\open\command' @{ '' = $command }
Set-Key 'Software\Classes\Applications\glosa.exe\SupportedTypes' $supported

# Settings > Apps > Default apps lists the programs registered here, with what they open.
$associations = @{}
foreach ($type in $types) { foreach ($extension in $type.Extensions) { $associations[$extension] = $type.Id } }
Set-Key 'Software\Glosa\Capabilities' @{
    ApplicationName        = 'Glosa'
    ApplicationIcon        = $icon
    ApplicationDescription = $(if ($ja) { '対象音源の自動判別とポートマップの自動切り替えに対応した MIDI プレイヤー' }
                               else { 'Plays MIDI files, choosing the ports by the module each song was written for' })
}
Set-Key 'Software\Glosa\Capabilities\FileAssociations' $associations
Set-Key 'Software\RegisteredApplications' @{ Glosa = 'Software\Glosa\Capabilities' }

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Glosa.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $root
$link.Description = 'Glosa'
$link.Save()

# Explorer caches what opens what; this tells it to look again.
if (-not ('Glosa.Shell' -as [type])) {
    Add-Type -Namespace Glosa -Name Shell -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, System.IntPtr item1, System.IntPtr item2);
'@
}
[Glosa.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)   # SHCNE_ASSOCCHANGED

Say 'Glosa is now in the Start menu and in "Open with" for MIDI and RCP files. If you move this folder, run install.cmd again.' `
    'Glosa をスタートメニューと、MIDI・RCP ファイルの「プログラムから開く」に登録しました。このフォルダを移動したら、もう一度 install.cmd を実行してください。'
Finish 0
