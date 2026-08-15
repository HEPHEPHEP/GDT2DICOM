using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace Gdt2Dicom.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Datums- und Zahlenformate in der Oberfläche in deutscher Schreibweise.
        var culture = new CultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Unerwarteter Fehler:\n\n{args.Exception.Message}",
                "GDT2DICOM", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
