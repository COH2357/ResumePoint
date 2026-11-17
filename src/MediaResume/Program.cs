using System.Text;
using MediaResume.Core;
using MediaResume.UI;

namespace MediaResume;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase)))
            return RunDiag(args);

        // 单实例：托盘常驻程序必须保证只有一个，否则会重复写库 / 重复 seek
        using var mutex = new Mutex(initiallyOwned: true, @"MediaResume.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // 已有实例在跑 -> 激活它（把主窗口叫出来）后退出
            TryActivateExisting();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppLog.Error("UI 线程异常", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("未处理异常", e.ExceptionObject as Exception);

        try
        {
            var settings = Settings.Load();
            settings.Save();
            Directory.CreateDirectory(settings.DataDirectory);

            var db = new ResumeDb(settings.DatabasePath);
            db.Open();

            var smtc = new Smtc();
            var hints = new LaunchHintStore();
            var recorder = new Recorder(settings, db, smtc, hints);
            var launcher = new PlayerLauncher(settings, db, smtc, hints);

            using var tray = new TrayHost(settings, db, recorder, launcher);
            Application.Run(tray.Context);

            db.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("启动失败", ex);
            MessageBox.Show(
                $"MediaResume 启动失败：\n\n{ex.Message}\n\n日志：{AppLog.LogPath}",
                "MediaResume", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>诊断模式：打印环境与 SMTC 状态，不启动托盘。</summary>
    private static int RunDiag(string[] args)
    {
        // 本程序是 WinExe（GUI，无控制台）——诊断模式挂接父控制台才能输出
        if (!NativeMethods.AttachConsole(-1))   // -1 = ATTACH_PARENT_PROCESS
        {
            try
            {
                var stdout = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
            }
            catch { }
        }

        Console.OutputEncoding = Encoding.UTF8;
        var settings = Settings.Load();
        var o = new StringBuilder();

        void W(string line)
        {
            o.AppendLine(line);
            try { Console.WriteLine(line); } catch { }
        }

        W("=== MediaResume 诊断 ===");
        W($"OS               : {Environment.OSVersion}");
        W($"设置文件         : {Settings.SettingsPath}");
        W($"数据目录         : {settings.DataDirectory}");
        W($"数据库           : {settings.DatabasePath}");
        W($"播放器落盘状态   : {PlayerState.StatePath}");

        var st = PlayerState.Read();
        W($"  可用           : {st.Available}");
        W($"  LastWrite      : {st.LastWrite}");
        W($"  当前队列项     : {st.CurrentSource ?? "(无)"}");
        W($"  队列           : {string.Join(" | ", st.QueueSources)}");
        W($"  落盘位置       : {st.Position}");

        var smtc = new Smtc();
        var sessions = smtc.GetSessionsAsync().GetAwaiter().GetResult();
        W($"\nSMTC 会话数      : {sessions.Count}");
        foreach (var s in sessions)
        {
            W($"--- {s.Aumid}{(s.IsTarget ? "   <<<< 新版媒体播放器" : "")}");
            W($"    状态         : {s.Status}");
            W($"    可Seek       : {s.SeekEnabled}");
            W($"    位置         : {s.Position} / {s.EndTime}");
            W($"    标题         : '{s.Title}'");
            W($"    艺术家       : '{s.Artist}'");
        }

        if (args.Any(a => a.Equals("--seek", StringComparison.OrdinalIgnoreCase)))
        {
            var idx = Array.FindIndex(args, a => a.Equals("--seek", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx + 1 < args.Length && double.TryParse(args[idx + 1], out var sec))
            {
                var ok = smtc.TrySeekAsync(TimeSpan.FromSeconds(sec)).GetAwaiter().GetResult();
                W($"\nTryChangePlaybackPositionAsync({sec}) = {ok}");
            }
        }

        // WinExe 没有控制台，输出同时写到文件，确保可读
        try
        {
            var diagPath = Path.Combine(settings.DataDirectory, "diag.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(diagPath)!);
            File.WriteAllText(diagPath, o.ToString(), Encoding.UTF8);
            Console.WriteLine($"(diagnostics written to {diagPath})");
        }
        catch { }

        return 0;
    }

    private static void TryActivateExisting()
    {
        // 通过广播一个自定义窗口消息把已有实例的窗口叫出来
        try
        {
            NativeMethods.BroadcastMessage(HWND_BROADCAST, NativeMethods.WM_APP + 1, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xffff);
}

internal static class NativeMethods
{
    public const int WM_APP = 0x8000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool AttachConsole(int dwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool BroadcastMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
}
