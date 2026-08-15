using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Gdt;

namespace Gdt2Dicom.Core.Pipeline;

/// <summary>
/// Wartet darauf, dass im Ausgangsverzeichnis ein Rücksatz für einen bestimmten Patienten
/// erscheint. Wird vom Connector gebraucht, wenn das PVS den Rücksatz nur unmittelbar nach
/// dem Programmende einliest.
/// </summary>
/// <remarks>
/// Bewusst über das Dateisystem statt über den Dienst: der Rücksatz ist das, worauf das PVS
/// wartet, also ist seine Existenz das ehrliche Kriterium. Das funktioniert auch dann noch,
/// wenn der Dienst zwischendurch neu startet.
/// </remarks>
public static class GdtResponseWaiter
{
    /// <summary>
    /// Sucht ab <paramref name="notBefore"/> nach einem Rücksatz mit der angegebenen
    /// Patientennummer. Gibt den Pfad zurück oder null, wenn Zeitlimit oder Abbruch zuerst kommen.
    /// </summary>
    /// <param name="timeout">
    /// <see cref="Timeout.InfiniteTimeSpan"/> oder ein negativer Wert bedeutet: kein Zeitlimit.
    /// Dann endet das Warten nur über <paramref name="cancellationToken"/>.
    /// </param>
    /// <param name="beforeEachScan">
    /// Wird vor jedem Durchsehen des Ausgangsverzeichnisses aufgerufen. Steht die Auslieferung
    /// auf „auf Abruf“, hält der Dienst den fertigen Rücksatz zurück – dann muss er hier
    /// angefordert werden, sonst erschiene im Ausgang nie etwas und das Warten liefe leer.
    /// </param>
    public static async Task<string?> WaitAsync(
        AppConfig config,
        string patientId,
        DateTime notBefore,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<Task>? beforeEachScan = null)
    {
        var gdt = config.Gdt;
        if (string.IsNullOrWhiteSpace(gdt.OutboxDirectory))
            return null;

        // Ohne Zeitlimit darf DateTime.UtcNow + timeout nicht gerechnet werden – das liefe über.
        var unbegrenzt = timeout <= TimeSpan.Zero;
        var deadline = unbegrenzt ? DateTime.MaxValue : DateTime.UtcNow + timeout;

        // Eine Sekunde Toleranz: Dateisystem-Zeitstempel und Prozessuhr laufen nicht
        // zwingend synchron, und ein knapp verpasster Rücksatz wäre der ärgerlichste Fall.
        var since = notBefore.AddSeconds(-1);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (beforeEachScan is not null)
                await beforeEachScan();

            var match = FindResponse(gdt, patientId, since);
            if (match is not null)
                return match;

            try
            {
                await Task.Delay(500, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        return null;
    }

    private static string? FindResponse(GdtConfig gdt, string patientId, DateTime since)
    {
        if (!Directory.Exists(gdt.OutboxDirectory))
            return null;

        IEnumerable<FileInfo> candidates;
        try
        {
            candidates = new DirectoryInfo(gdt.OutboxDirectory)
                .GetFiles("*.gdt")
                .Where(f => f.LastWriteTimeUtc >= since)
                .OrderByDescending(f => f.LastWriteTimeUtc);
        }
        catch (IOException)
        {
            return null;
        }

        foreach (var file in candidates)
        {
            try
            {
                // GdtSerializer schreibt über eine temporäre Datei und benennt um; eine
                // sichtbare .gdt-Datei ist daher immer vollständig.
                var record = GdtSerializer.ReadFile(file.FullName, gdt.Charset);

                var satzart = record.Get(gdt.FieldMap.Satzidentifikation);
                if (!string.Equals(satzart, gdt.ResponseSatzart, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(patientId))
                    return file.FullName;

                var found = record.Get(gdt.FieldMap.PatientId)?.Trim();
                if (string.Equals(found, patientId.Trim(), StringComparison.OrdinalIgnoreCase))
                    return file.FullName;
            }
            catch (IOException)
            {
                // Wird gerade geschrieben – beim nächsten Durchlauf erneut ansehen.
            }
            catch (Exception)
            {
                // Unlesbare Fremddatei im Ausgangsverzeichnis: überspringen.
            }
        }

        return null;
    }
}
