# 启动 Win11 新版媒体播放器并加载指定文件（多条策略依次尝试）
param(
    [Parameter(Mandatory=$true)][string]$FilePath,
    [string]$Aumid = 'Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic',
    [string]$PackageFamilyName = 'Microsoft.ZuneMusic_8wekyb3d8bbwe'
)

$ErrorActionPreference = 'Continue'

Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null
$am = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]

function Await($op, [Type]$t) {
    $task = $am.MakeGenericMethod($t).Invoke($null, @($op))
    if (-not $task.Wait(20000)) { throw 'timeout 20s' }
    $task.Result
}

function Try-WinRtLauncher {
    Write-Host '[策略A] WinRT Launcher.LaunchFileAsync + TargetApplicationPackageFamilyName'
    try {
        [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
        [Windows.System.Launcher, Windows.System, ContentType = WindowsRuntime] | Out-Null
        [Windows.System.LauncherOptions, Windows.System, ContentType = WindowsRuntime] | Out-Null

        $fileOp = [Windows.Storage.StorageFile]::GetFileFromPathAsync($FilePath)
        $file = Await $fileOp ([Windows.Storage.StorageFile])
        Write-Host "  StorageFile: $($file.Path)"

        $opts = [Windows.System.LauncherOptions]::new()
        $opts.TargetApplicationPackageFamilyName = $PackageFamilyName

        $launchOp = [Windows.System.Launcher]::LaunchFileAsync($file, $opts)
        $ok = Await $launchOp ([bool])
        Write-Host "  LaunchFileAsync returned: $ok"
        if ($ok) { return $true }
    } catch {
        Write-Host "  策略A失败: $($_.Exception.Message)"
    }
    return $false
}

function Try-ComActivateForFile {
    Write-Host '[策略B] COM IApplicationActivationManager::ActivateForFile'
    try {
        $code = @'
using System;
using System.Runtime.InteropServices;

public class Win32Activator
{
    [ComImport, Guid("2e941141-7f85-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([In] string appUserModelId, [In] string arguments, [In] uint options, [Out] out uint processId);
        [PreserveSig] int ActivateForFile([In] string appUserModelId, [In, MarshalAs(UnmanagedType.Interface)] object itemArray, [In] string verb, [Out] out uint processId);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159e800858"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemArray
    {
        void GetPropertyStore();
        void GetPropertyDescriptionList();
        void GetAttributes();
        uint GetCount();
        IntPtr GetItemAt(uint index);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("shell32.dll", PreserveSig = false)]
    static extern void SHCreateShellItemArrayFromItems([MarshalAs(UnmanagedType.Interface)] object pItem, uint cItems, IntPtr rgItems, IntPtr apidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object itemArray);

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ApplicationActivationManager { }

    public static string ActivateForFile(string aumid, string path)
    {
        Guid iidItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        object item;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out item);

        Guid iidArray = new Guid("b63ea76d-1f85-456f-a19c-48159e800858");
        object array;
        SHCreateShellItemArrayFromItems(item, 1, IntPtr.Zero, IntPtr.Zero, ref iidArray, out array);

        var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
        uint pid;
        int hr = mgr.ActivateForFile(aumid, array, null, out pid);
        return "hr=0x" + hr.ToString("X8") + " pid=" + pid;
    }
}
'@
        Add-Type -TypeDefinition $code -ErrorAction Stop
        $res = [Win32Activator]::ActivateForFile($Aumid, $FilePath)
        Write-Host "  ActivateForFile -> $res"
        if ($res -match 'hr=0x0') { return $true }
    } catch {
        Write-Host "  策略B失败: $($_.Exception.Message)"
    }
    return $false
}

if (-not (Test-Path -LiteralPath $FilePath)) {
    Write-Host "文件不存在: $FilePath"
    exit 1
}

Write-Host "目标文件: $FilePath"
Write-Host "目标 AUMID: $Aumid"
Write-Host ''

$ok = Try-WinRtLauncher
if (-not $ok) { $ok = Try-ComActivateForFile }

Write-Host ''
if ($ok) {
    Write-Host 'RESULT: LAUNCH_OK'
} else {
    Write-Host 'RESULT: LAUNCH_FAILED'
}
