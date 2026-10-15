using System.Runtime.InteropServices;
using System.Windows;

namespace SideLoadr;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (CliParser.IsCli(e.Args))
        {
            AttachConsole(-1);
            int code = 2;
            try
            {
                code = await CliRunner.RunAsync(e.Args, Console.WriteLine, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine("error: " + ex.Message);
            }
            Shutdown(code);
            return;
        }
        base.OnStartup(e);
    }
}
