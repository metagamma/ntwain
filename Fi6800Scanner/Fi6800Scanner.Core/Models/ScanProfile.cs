using System.Collections.Generic;
using NTwain.Data;

namespace Fi6800Scanner.Core.Models
{
    public class ScanProfile
    {
        public string Name { get; set; } = "Default";

        // Imagen
        public PixelType PixelType { get; set; } = PixelType.RGB;
        public bool AutoColor { get; set; } = false;
        public PixelType AutoColorNonColorMode { get; set; } = PixelType.Gray;
        public int Brightness { get; set; } = 0;
        public int Contrast { get; set; } = 0;

        // Resolución y tamaño
        public int Dpi { get; set; } = 300;
        public SupportedSize PaperSize { get; set; } = SupportedSize.A4;
        public bool LongPage { get; set; } = false;
        public int LongPageMaxLengthMm { get; set; } = 3175;

        // Duplex / feeder
        public bool Duplex { get; set; } = true;
        public int PageCount { get; set; } = -1; // -1 = todas

        // Auto procesos
        public bool AutoDeskew { get; set; } = true;
        public bool AutoRotate { get; set; } = true;
        public bool AutoBorder { get; set; } = true;
        public bool DiscardBlankPages { get; set; } = false;
        public int? Rotation90Deg { get; set; }

        // Compresión / formato
        public CompressionType Compression { get; set; } = CompressionType.None;
        public int JpegQuality { get; set; } = 85;
        public XferMech XferMech { get; set; } = XferMech.File;
        public FileFormat FileFormat { get; set; } = FileFormat.Tiff;

        // Detecciones
        public bool PatchEnable { get; set; } = false;
        public List<PatchCode> PatchTypes { get; set; } = new List<PatchCode>();
        public bool BarcodeEnable { get; set; } = false;
        public List<BarcodeType> BarcodeTypes { get; set; } = new List<BarcodeType>();

        // Multifeed
        public MultifeedMode Multifeed { get; set; } = MultifeedMode.Ultrasonic;

        // Imprinter
        public ImprinterConfig Imprinter { get; set; }

        // Drop-out (solo bitonal)
        public FilterType? Filter { get; set; }
    }
}
