# Takes away what install.cmd registered, leaving the program in this folder. Run it
# through uninstall.cmd. Settings kept in a folder of your own choosing (--config) are not
# looked for.
#
# -RegistrationsOnly is what install.ps1 runs first: the registrations go, and nothing is
# asked or said.
param([switch] $RegistrationsOnly)

$ErrorActionPreference = 'Stop'

$config = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'glosa'
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

$user = [Microsoft.Win32.Registry]::CurrentUser

function Remove-Key([string] $path) { $user.DeleteSubKeyTree($path, $false) }

function Remove-Value([string] $path, [string] $name) {
    $key = $user.OpenSubKey($path, $true)
    if (-not $key) { return }
    try { $key.DeleteValue($name, $false) } finally { $key.Dispose() }
}

function Remove-IfEmpty([string] $path) {
    $key = $user.OpenSubKey($path)
    if (-not $key) { return }
    try { $empty = $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0 } finally { $key.Dispose() }
    if ($empty) { $user.DeleteSubKey($path, $false) }
}

$types = @{
    'Glosa.MIDI' = '.mid', '.midi'
    'Glosa.RMI'  = , '.rmi'
    'Glosa.RCP'  = '.rcp', '.r36', '.g18', '.g36'
}
foreach ($id in $types.Keys) {
    # A key with nothing left in it was made for Glosa alone: the extension's list, and the
    # extension itself when this user had nothing else set for it.
    foreach ($extension in $types[$id]) {
        Remove-Value "Software\Classes\$extension\OpenWithProgids" $id
        Remove-IfEmpty "Software\Classes\$extension\OpenWithProgids"
        Remove-IfEmpty "Software\Classes\$extension"
    }
    Remove-Key "Software\Classes\$id"
}
Remove-Key 'Software\Classes\Applications\glosa.exe'
Remove-Value 'Software\RegisteredApplications' 'Glosa'
Remove-Key 'Software\Glosa'

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Glosa.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }

# Once per session: install.ps1 runs this first, and a type cannot be added twice.
if (-not ('Glosa.Shell' -as [type])) {
    Add-Type -Namespace Glosa -Name Shell -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, System.IntPtr item1, System.IntPtr item2);
'@
}
[Glosa.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)   # SHCNE_ASSOCCHANGED

if ($RegistrationsOnly) { return }

Say 'Glosa is no longer in the Start menu or in "Open with". To remove the program itself, delete this folder.' `
    'Glosa をスタートメニューと「プログラムから開く」から外しました。プログラム自体を削除するには、このフォルダを削除してください。'

if (Test-Path -LiteralPath $config -PathType Container) {
    if (Get-Process -Name glosa -ErrorAction SilentlyContinue) {
        Say "Glosa is running, so the settings folder $config was left alone. Quit it and run this again to delete it." `
            "Glosa が起動しているため、設定フォルダ $config はそのままにしました。削除するには、Glosa を終了してから、もう一度実行してください。"
    }
    elseif (Ask "Also delete the settings folder $config and everything in it (settings, playlists, define.override.yaml and any other files)? [y/N]" `
                "設定フォルダ $config も、中のファイルごと削除しますか？（設定・プレイリスト・define.override.yaml など、すべて） [y/N]") {
        Remove-Item -LiteralPath $config -Recurse -Force
        Say 'The settings folder has been deleted.' '設定フォルダを削除しました。'
    }
    else {
        Say "The settings folder is still at $config." "設定フォルダは $config に残っています。"
    }
}
Finish 0
