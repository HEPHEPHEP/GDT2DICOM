using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Ipc;

namespace Gdt2Dicom.Gui;

public sealed record EnumOption(object Value, string Label);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private AppConfig _config = new();
    private StatusDto? _status;
    private string _logText = "";
    private string _serviceStateText = "wird ermittelt …";
    private string _connectionText = "";
    private bool _hasUnsavedChanges;
    private string _statusMessage = "";

    public AppConfig Config
    {
        get => _config;
        set
        {
            _config = value;
            AllowedCallingAes = string.Join(", ", value.Dicom.AllowedCallingAeTitles);
            AdditionalSatzarten = string.Join(", ", value.Gdt.AdditionalRequestSatzarten);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AllowedCallingAes));
            OnPropertyChanged(nameof(AdditionalSatzarten));
        }
    }

    public StatusDto? Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status is not null;

    public string LogText
    {
        get => _logText;
        set { _logText = value; OnPropertyChanged(); }
    }

    public string ServiceStateText
    {
        get => _serviceStateText;
        set { _serviceStateText = value; OnPropertyChanged(); }
    }

    public string ConnectionText
    {
        get => _connectionText;
        set { _connectionText = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set { _hasUnsavedChanges = value; OnPropertyChanged(); }
    }

    public ObservableCollection<WorklistRow> Worklist { get; } = new();
    public ObservableCollection<PendingStudyDto> PendingStudies { get; } = new();

    // --- Listen, die im UI als Komma-Text bearbeitet werden ---

    private string _allowedCallingAes = "";
    public string AllowedCallingAes
    {
        get => _allowedCallingAes;
        set
        {
            _allowedCallingAes = value;
            Config.Dicom.AllowedCallingAeTitles = SplitList(value);
            OnPropertyChanged();
        }
    }

    private string _additionalSatzarten = "";
    public string AdditionalSatzarten
    {
        get => _additionalSatzarten;
        set
        {
            _additionalSatzarten = value;
            Config.Gdt.AdditionalRequestSatzarten = SplitList(value);
            OnPropertyChanged();
        }
    }

    private static List<string> SplitList(string value) =>
        value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // --- Auswahllisten für ComboBoxen ---

    public IReadOnlyList<EnumOption> GdtVersions { get; } = new[]
    {
        new EnumOption(GdtVersion.V21, "GDT 2.1  (02.10)"),
        new EnumOption(GdtVersion.V30, "GDT 3.0  (03.00)"),
        new EnumOption(GdtVersion.V31, "GDT 3.1  (03.10)")
    };

    public IReadOnlyList<EnumOption> Charsets { get; } = new[]
    {
        new EnumOption(GdtCharset.Iso8859_1, "ISO 8859-1 / ANSI  (FK 9206 = 3)"),
        new EnumOption(GdtCharset.Cp437, "IBM CP437 / DOS  (FK 9206 = 2)"),
        new EnumOption(GdtCharset.Ascii7, "7-Bit-ASCII  (FK 9206 = 1)"),
        new EnumOption(GdtCharset.Utf8, "UTF-8  (FK 9206 = 4)")
    };

    public IReadOnlyList<EnumOption> ImageFormats { get; } = new[]
    {
        new EnumOption(ImageOutputFormat.Jpeg, "JPEG"),
        new EnumOption(ImageOutputFormat.Png, "PNG"),
        new EnumOption(ImageOutputFormat.None, "keine Einzelbilder")
    };

    public IReadOnlyList<EnumOption> DeliveryModes { get; } = new[]
    {
        new EnumOption(ResponseDelivery.Sofort, "sofort ins Ausgangsverzeichnis"),
        new EnumOption(ResponseDelivery.AufAbruf, "erst wenn das PVS ihn abholt")
    };

    public IReadOnlyList<EnumOption> PdfFormats { get; } = new[]
    {
        new EnumOption(PdfFormat.Standard, "PDF (normal)"),
        new EnumOption(PdfFormat.PdfA3b, "PDF/A-3b (für die ePA)")
    };

    public IReadOnlyList<EnumOption> PathModes { get; } = new[]
    {
        new EnumOption(AttachmentPathMode.Absolute, "vollständiger Pfad"),
        new EnumOption(AttachmentPathMode.RelativeToOutbox, "relativ zum GDT-Ausgang"),
        new EnumOption(AttachmentPathMode.FileNameOnly, "nur Dateiname")
    };

    public IReadOnlyList<EnumOption> AccessionModes { get; } = new[]
    {
        new EnumOption(AccessionNumberMode.FromGdtElseGenerated, "aus dem GDT-Satz, sonst fortlaufend"),
        new EnumOption(AccessionNumberMode.AlwaysGenerated, "immer fortlaufend erzeugen"),
        new EnumOption(AccessionNumberMode.PatientIdAndDate, "Patienten-Nr. + Datum")
    };

    public IReadOnlyList<string> LogLevels { get; } = new[] { "Verbose", "Debug", "Information", "Warning", "Error" };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
