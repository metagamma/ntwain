using System;
using System.Collections.Generic;
using Fi6800Scanner.Core.Models;
using NTwain;
using NTwain.Data;

namespace Fi6800Scanner.Core.Capabilities
{
    /// <summary>
    /// Aplica un ScanProfile al DataSource respetando el orden correcto:
    /// XferMech → PixelType/AutoColor → BitDepth → Resolution → Geometry → Auto-procesos →
    /// Imagen ajustes → Compresión → Detecciones → Multifeed → Imprinter.
    /// </summary>
    public static class ProfileApplicator
    {
        public class ApplyReport
        {
            public List<string> Applied { get; } = new List<string>();
            public List<string> Skipped { get; } = new List<string>();
            public List<string> Failed { get; } = new List<string>();
        }

        public static ApplyReport Apply(DataSource source, ScanProfile p, Fi6800CapabilitySnapshot snap = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (p == null) throw new ArgumentNullException(nameof(p));

            var report = new ApplyReport();
            var c = source.Capabilities;

            // 1) XferMech
            TrySet(c.ICapXferMech, p.XferMech, "XferMech", report);

            // 2) PixelType o AutoColor
            if (p.AutoColor && c.ICapAutomaticColorEnabled.IsSupported)
            {
                SetBool(c.ICapAutomaticColorEnabled, true, "AutoColor", report);
                TrySet(c.ICapAutomaticColorNonColorPixelType, p.AutoColorNonColorMode, "AutoColorNonColor", report);
            }
            else
            {
                if (c.ICapAutomaticColorEnabled.IsSupported)
                    SetBool(c.ICapAutomaticColorEnabled, false, "AutoColor=Off", report);
                TrySet(c.ICapPixelType, p.PixelType, "PixelType", report);
            }

            // 3) Resolución
            TrySet(c.ICapXResolution, (TWFix32)(float)p.Dpi, "DpiX", report);
            TrySet(c.ICapYResolution, (TWFix32)(float)p.Dpi, "DpiY", report);

            // 4) Tamaño / long page
            if (p.LongPage)
            {
                TrySet(c.ICapSupportedSizes, SupportedSize.None, "PaperSize=None(longpage)", report);
                // ICapPhysicalHeight es read-only — long page real requiere DGControl directo.
                report.Skipped.Add("LongPage: ICapPhysicalHeight es read-only en NTwain v3 — usar DGControl.Capability directo o cap custom de PaperStream para fijar largo");
            }
            else
            {
                TrySet(c.ICapSupportedSizes, p.PaperSize, "PaperSize", report);
            }

            // 5) Duplex / feeder
            SetBool(c.CapFeederEnabled, true, "Feeder", report);
            SetBool(c.CapAutoFeed, true, "AutoFeed", report);
            SetBool(c.CapDuplexEnabled, p.Duplex, "Duplex", report);
            TrySet(c.CapXferCount, p.PageCount, "XferCount", report);

            // 6) Auto-procesos (BorderDetection → Deskew → Rotation/AutoRotate)
            SetBool(c.ICapAutomaticBorderDetection, p.AutoBorder, "AutoBorder", report);
            SetBool(c.ICapAutomaticDeskew,          p.AutoDeskew, "AutoDeskew", report);

            if (p.Rotation90Deg.HasValue)
            {
                if (c.ICapAutomaticRotate.IsSupported)
                    SetBool(c.ICapAutomaticRotate, false, "AutoRotate=Off(manual)", report);
                TrySet(c.ICapRotation, (TWFix32)(float)p.Rotation90Deg.Value, "Rotation", report);
            }
            else if (p.AutoRotate)
            {
                SetBool(c.ICapAutomaticRotate, true, "AutoRotate", report);
            }

            if (c.ICapAutoDiscardBlankPages.IsSupported)
            {
                // BlankPage enum: Disable=-2, Auto=-1
                TrySet(c.ICapAutoDiscardBlankPages,
                    p.DiscardBlankPages ? BlankPage.Auto : BlankPage.Disable,
                    "DiscardBlank", report);
            }

            // 7) Imagen ajustes
            if (c.ICapBrightness.IsSupported)
                TrySet(c.ICapBrightness, (TWFix32)(float)p.Brightness, "Brightness", report);
            if (c.ICapContrast.IsSupported)
                TrySet(c.ICapContrast, (TWFix32)(float)p.Contrast, "Contrast", report);

            if (p.Filter.HasValue && c.ICapFilter.IsSupported)
                TrySet(c.ICapFilter, p.Filter.Value, "Filter", report);

            // 8) Compresión y formato
            if (c.ICapCompression.IsSupported)
                TrySet(c.ICapCompression, p.Compression, "Compression", report);
            if (p.Compression == CompressionType.Jpeg && c.ICapJpegQuality.IsSupported)
                TrySet(c.ICapJpegQuality, (JpegQuality)p.JpegQuality, "JpegQuality", report);
            if (c.ICapImageFileFormat.IsSupported)
                TrySet(c.ICapImageFileFormat, p.FileFormat, "FileFormat", report);

            // 9) Patch codes
            if (c.ICapPatchCodeDetectionEnabled.IsSupported)
            {
                SetBool(c.ICapPatchCodeDetectionEnabled, p.PatchEnable, p.PatchEnable ? "PatchEnable" : "PatchEnable=Off", report);
                if (p.PatchEnable && c.CapJobControl.IsSupported)
                    TrySet(c.CapJobControl, JobControl.IncludeStop, "JobControl=IncludeStop", report);
            }

            // 10) Barcodes
            if (c.ICapBarcodeDetectionEnabled.IsSupported)
                SetBool(c.ICapBarcodeDetectionEnabled, p.BarcodeEnable, p.BarcodeEnable ? "BarcodeEnable" : "BarcodeEnable=Off", report);

            // 11) Multifeed
            ApplyMultifeed(c, p.Multifeed, report);

            // 12) Imprinter
            if (p.Imprinter != null && p.Imprinter.Enabled && c.CapPrinterEnabled.IsSupported)
            {
                SetBool(c.CapPrinterEnabled, true, "PrinterEnable", report);
                if (c.CapPrinterString.IsSupported && !string.IsNullOrEmpty(p.Imprinter.Text))
                    TrySet(c.CapPrinterString, p.Imprinter.Text, "PrinterString", report);
                if (c.CapPrinterIndex.IsSupported)
                    TrySet(c.CapPrinterIndex, p.Imprinter.CounterStart, "PrinterIndex", report);
            }

            return report;
        }

        private static void ApplyMultifeed(ICapabilities c, MultifeedMode mode, ApplyReport report)
        {
            if (!c.CapDoubleFeedDetection.IsSupported)
            {
                report.Skipped.Add("Multifeed: CapDoubleFeedDetection no soportada");
                return;
            }

            try
            {
                switch (mode)
                {
                    case MultifeedMode.Off:
                        report.Skipped.Add("Multifeed=Off (depende del driver setear None)");
                        break;
                    case MultifeedMode.Ultrasonic:
                        TrySet(c.CapDoubleFeedDetection, DoubleFeedDetection.Ultrasonic, "Multifeed=Ultrasonic", report);
                        break;
                    case MultifeedMode.ByLength:
                        TrySet(c.CapDoubleFeedDetection, DoubleFeedDetection.ByLength, "Multifeed=ByLength", report);
                        break;
                    case MultifeedMode.Both:
                        // Algunos drivers requieren Set dos veces para combinar, otros tienen un flag combinado custom.
                        TrySet(c.CapDoubleFeedDetection, DoubleFeedDetection.Ultrasonic, "Multifeed=Ultrasonic", report);
                        TrySet(c.CapDoubleFeedDetection, DoubleFeedDetection.ByLength, "Multifeed+=ByLength", report);
                        break;
                }
            }
            catch (Exception ex)
            {
                report.Failed.Add("Multifeed: " + ex.Message);
            }
        }

        // Helper específico para BoolType — evita problemas de inferencia con ternario
        private static void SetBool(ICapWrapper<BoolType> cap, bool value, string label, ApplyReport report)
        {
            BoolType bv = value ? BoolType.True : BoolType.False;
            TrySet<BoolType>(cap, bv, label, report);
        }

        private static bool TrySet<T>(ICapWrapper<T> cap, T value, string label, ApplyReport report)
        {
            if (cap == null || !cap.IsSupported) { report.Skipped.Add(label + " (no soportado)"); return false; }
            if (!cap.CanSet) { report.Skipped.Add(label + " (read-only)"); return false; }
            try
            {
                var rc = cap.SetValue(value);
                if (rc == ReturnCode.Success)
                {
                    report.Applied.Add(label + " = " + value);
                    return true;
                }
                report.Failed.Add(label + " → " + rc);
                return false;
            }
            catch (Exception ex)
            {
                report.Failed.Add(label + " → " + ex.Message);
                return false;
            }
        }
    }
}
