namespace Gdt2Dicom.Core.Gdt;

/// <summary>
/// Feldkennungen, die im Code fest verdrahtet sein müssen, weil sie das Dateiformat
/// selbst betreffen. Alle inhaltlichen Kennungen kommen aus <see cref="Configuration.GdtFieldMap"/>.
/// </summary>
public static class GdtFk
{
    public const string Satzidentifikation = "8000";
    public const string Satzlaenge = "8100";
    public const string EmpfaengerId = "8315";
    public const string SenderId = "8316";
    public const string Zeichensatz = "9206";
    public const string GdtVersion = "9218";
    public const string ObjektBeginn = "8200";
    public const string ObjektEnde = "8201";
}

/// <summary>Bekannte Satzarten.</summary>
public static class GdtSatzart
{
    /// <summary>Stammdaten übermitteln.</summary>
    public const string StammdatenUebermitteln = "6300";
    /// <summary>Stammdaten anfordern.</summary>
    public const string StammdatenAnfordern = "6301";
    /// <summary>Neue Untersuchung anfordern (PVS → Gerät).</summary>
    public const string UntersuchungAnfordern = "6302";
    /// <summary>Daten einer Untersuchung übermitteln (Gerät → PVS).</summary>
    public const string UntersuchungUebermitteln = "6310";
    /// <summary>Daten einer Untersuchung anfordern.</summary>
    public const string UntersuchungAnfordernDaten = "6311";
}
