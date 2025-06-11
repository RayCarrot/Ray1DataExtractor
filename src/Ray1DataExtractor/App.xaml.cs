using System.Text;
using System.Windows;

namespace Ray1DataExtractor;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Register encoding provider to be able to use Windows 1252
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}