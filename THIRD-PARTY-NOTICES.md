# Hinweise zu Fremdkomponenten

GDT2DICOM selbst steht unter der GNU General Public License, Version 3 oder später
(siehe [LICENSE](LICENSE)). Die folgenden Bibliotheken werden mitgeliefert und stehen
unter ihren eigenen Bedingungen. Deren Urheberrechtsvermerke bleiben davon unberührt.

| Komponente | Version | Lizenz |
|---|---|---|
| [fo-dicom](https://github.com/fo-dicom/fo-dicom) | 5.2.6 | MS-PL |
| [fo-dicom.Imaging.Desktop](https://github.com/fo-dicom/fo-dicom) | 5.2.6 | MS-PL |
| [fo-dicom.Codecs](https://github.com/Efferent-Health/fo-dicom.Codecs) | 5.16.7 | MS-PL, enthält Codecs unter weiteren freien Lizenzen |
| [PDFsharp](https://github.com/empira/PDFsharp) | 6.2.4 | MIT |
| [Serilog](https://github.com/serilog/serilog) | 4.4.0 | Apache-2.0 |
| Serilog.Extensions.Logging | 9.0.2 | Apache-2.0 |
| Serilog.Sinks.Console | 6.1.1 | Apache-2.0 |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 |
| Microsoft.Extensions.Hosting | 10.0.11 | MIT |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.11 | MIT |
| System.ServiceProcess.ServiceController | 10.0.11 | MIT |
| .NET-Laufzeit (im Paket enthalten, self-contained) | 10.0 | MIT |

## MS-PL neben der GPL

fo-dicom steht unter der **Microsoft Public License**. Die Free Software Foundation
stuft die MS-PL als freie, aber **nicht GPL-verträgliche** Lizenz ein: MS-PL § 3 (D)
verlangt, dass der abgedeckte Code nur unter MS-PL-Bedingungen weitergegeben wird,
während die GPL für das Gesamtwerk GPL-Bedingungen verlangt. Beides zugleich lässt sich
nicht erfüllen.

Betroffen ist nur die **Weitergabe** übersetzter Pakete, in denen beides zusammenkommt –
also insbesondere das MSI. Der Quellcode für sich genommen wirft die Frage nicht auf.

Der Rechteinhaber hat deshalb eine **zusätzliche Erlaubnis nach GPL § 7** erteilt, die das
Binden gegen genau diese Bibliotheken gestattet. Wortlaut und Reichweite stehen in
[LICENSE-EXCEPTION.md](LICENSE-EXCEPTION.md); im Installationsverzeichnis liegt sie als
`LICENSE-EXCEPTION.txt`. Die Pflicht zur Herausgabe des Quellcodes bei Weitergabe eines
übersetzten Pakets bleibt bestehen und erstreckt sich auf die mitverwendeten Teile dieser
Bibliotheken.
