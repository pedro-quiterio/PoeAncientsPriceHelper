using Velopack;

namespace PoeAncientsPriceHelper;

// Explicit process entry point. Velopack requires VelopackApp.Build().Run() to be the very first
// thing that executes: on install/update/uninstall the app is relaunched with hook arguments
// (e.g. --veloapp-install) that this call handles and then exits, before any of our own startup
// (single-instance mutex, --ocr-test, --debug console) runs. With no hook args it returns and we
// start WPF exactly the way the auto-generated Main used to (App.xaml is now a Page, see csproj).
internal static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
