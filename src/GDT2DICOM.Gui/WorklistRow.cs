using System.ComponentModel;
using System.Runtime.CompilerServices;
using Gdt2Dicom.Core.Ipc;

namespace Gdt2Dicom.Gui;

/// <summary>
/// Eine Zeile der Worklist-Anzeige.
/// </summary>
/// <remarks>
/// Bewusst eine eigene Klasse statt des IPC-Datenobjekts: Die Liste aktualisiert sich alle
/// zwei Sekunden. Ohne Änderungsbenachrichtigung müsste das Gitter dafür komplett neu
/// gelesen werden (<c>Items.Refresh()</c>), und dabei geht jedes Mal die Auswahl verloren –
/// mehrere Einträge zum Löschen zu markieren wäre damit unmöglich.
/// </remarks>
public sealed class WorklistRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string AccessionNumber { get; init; } = "";
    public string PatientId { get; init; } = "";
    public string PatientName { get; init; } = "";
    public string BirthDate { get; init; } = "";
    public string ScheduledDate { get; init; } = "";
    public string ScheduledTime { get; init; } = "";
    public string Procedure { get; init; } = "";

    private string _state = "";
    public string State
    {
        get => _state;
        set
        {
            if (_state == value)
                return;
            _state = value;
            OnPropertyChanged();
        }
    }

    private int _queryCount;
    public int QueryCount
    {
        get => _queryCount;
        set
        {
            if (_queryCount == value)
                return;
            _queryCount = value;
            OnPropertyChanged();
        }
    }

    public static WorklistRow From(WorklistItemDto dto) => new()
    {
        Id = dto.Id,
        AccessionNumber = dto.AccessionNumber,
        PatientId = dto.PatientId,
        PatientName = dto.PatientName,
        BirthDate = dto.BirthDate,
        ScheduledDate = dto.ScheduledDate,
        ScheduledTime = dto.ScheduledTime,
        Procedure = dto.Procedure,
        State = dto.State,
        QueryCount = dto.QueryCount
    };

    /// <summary>Übernimmt die veränderlichen Werte, ohne die Objektidentität aufzugeben.</summary>
    public void Update(WorklistItemDto dto)
    {
        State = dto.State;
        QueryCount = dto.QueryCount;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
