using System;
using System.Collections.Generic;
using NTwain.Data;

namespace Fi6800Scanner.Core.Models
{
    public class Fi6800CapabilitySnapshot
    {
        public string ScannerName { get; set; }
        public string Manufacturer { get; set; }
        public string ProductFamily { get; set; }
        public string DriverVersion { get; set; }
        public string ProtocolVersion { get; set; }
        public DateTime ProbedAt { get; set; }

        // IDs raw soportadas
        public List<string> SupportedCapIds { get; set; } = new List<string>();
        public List<string> CustomCapIds { get; set; } = new List<string>();

        // Modos de imagen
        public List<PixelType> PixelTypes { get; set; } = new List<PixelType>();
        public List<int> BitDepths { get; set; } = new List<int>();

        // Resolución
        public List<float> ResolutionValues { get; set; } = new List<float>();
        public float? ResolutionMin { get; set; }
        public float? ResolutionMax { get; set; }

        // Geometría
        public List<SupportedSize> SupportedSizes { get; set; } = new List<SupportedSize>();
        public float? PhysicalWidthInches { get; set; }
        public float? PhysicalHeightInches { get; set; }

        // Compresión / formatos
        public List<CompressionType> Compressions { get; set; } = new List<CompressionType>();
        public List<FileFormat> FileFormats { get; set; } = new List<FileFormat>();

        // Detecciones
        public List<PatchCode> PatchCodes { get; set; } = new List<PatchCode>();
        public List<BarcodeType> Barcodes { get; set; } = new List<BarcodeType>();
        public List<ExtendedImageInfo> ExtImageInfo { get; set; } = new List<ExtendedImageInfo>();

        // Booleans simples por feature
        public Features Features { get; set; } = new Features();

        // Caps custom inspeccionadas
        public List<CustomCapDescriptor> CustomCaps { get; set; } = new List<CustomCapDescriptor>();
    }

    public class Features
    {
        public bool Duplex { get; set; }
        public bool AutoColor { get; set; }
        public bool AutoDeskew { get; set; }
        public bool AutoRotate { get; set; }
        public bool AutoBorder { get; set; }
        public bool AutoLengthDetection { get; set; }
        public bool DiscardBlank { get; set; }
        public bool DoubleFeedDetection { get; set; }
        public bool DoubleFeedByLength { get; set; }
        public bool DoubleFeedByUltrasonic { get; set; }
        public bool Imprinter { get; set; }
        public bool DigitalEndorser { get; set; }
        public bool PatchDetection { get; set; }
        public bool BarcodeDetection { get; set; }
        public bool JobControl { get; set; }
        public bool CustomDsData { get; set; }
        public bool ExtImageInfo { get; set; }
        public bool Threshold { get; set; }
        public bool Brightness { get; set; }
        public bool Contrast { get; set; }
        public bool Gamma { get; set; }
        public bool Filter { get; set; }
        public bool JpegQuality { get; set; }
        public bool JpegSubsampling { get; set; }
        public bool Mirror { get; set; }
        public bool Rotation { get; set; }
        public bool Orientation { get; set; }
        public bool LongPageDetected { get; set; }
        public bool MultiStreamDetected { get; set; }
    }

    public class CustomCapDescriptor
    {
        public string Id { get; set; }
        public int IdNumeric { get; set; }
        public string Label { get; set; }
        public string Help { get; set; }
        public string CurrentValue { get; set; }
        public string DefaultValue { get; set; }
        public List<string> SupportedValues { get; set; } = new List<string>();
        public string Notes { get; set; }
    }
}
