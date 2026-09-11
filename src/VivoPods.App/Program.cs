using Avalonia;

namespace VivoPods.App;

internal static class Program
{
    public static string[] Arguments { get; private set; } = [];
    [STAThread]
    public static void Main(string[] args)
    {
        Arguments = args;
        using var mutex = new Mutex(true, "Local\\VivoPodsManager.Desktop", out bool first);
        if (!first && !args.Contains("--smoke"))
        {
            try { using var signal = EventWaitHandle.OpenExisting("Local\\VivoPodsManager.Show"); signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
