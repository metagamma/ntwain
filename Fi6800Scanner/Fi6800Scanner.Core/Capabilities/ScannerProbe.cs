using System;
using System.Collections.Generic;
using System.Linq;
using Fi6800Scanner.Core.Models;
using NTwain;
using NTwain.Data;

namespace Fi6800Scanner.Core.Capabilities
{
    /// <summary>
    /// Inspecciona runtime las capabilities del DataSource y produce un snapshot.
    /// Diseñado para no asumir nada — todo es probing.
    /// </summary>
    public class ScannerProbe
    {
        public Fi6800CapabilitySnapshot Probe(DataSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!source.IsOpen) throw new InvalidOperationException("Source debe estar abierto para hacer probe.");

            var snap = new Fi6800CapabilitySnapshot
            {
                ScannerName     = source.Name,
                Manufacturer    = source.Manufacturer,
                ProductFamily   = source.ProductFamily,
                DriverVersion   = source.Version.Info,
                ProtocolVersion = source.ProtocolVersion != null ? source.ProtocolVersion.ToString() : null,
                ProbedAt        = DateTime.UtcNow
            };

            ProbeSupportedCaps(source, snap);
            ProbePixelAndBitDepth(source, snap);
            ProbeResolution(source, snap);
            ProbeGeometry(source, snap);
            ProbeCompressionAndFormat(source, snap);
            ProbePatchCodes(source, snap);
            ProbeBarcodes(source, snap);
            ProbeExtImageInfo(source, snap);
            ProbeFeatures(source, snap);
            ProbeCustomCaps(source, snap);

            return snap;
        }

        private static void ProbeSupportedCaps(DataSource source, Fi6800CapabilitySnapshot snap)
        {
            var cap = source.Capabilities.CapSupportedCaps;
            if (!cap.IsSupported) return;

            var ids = cap.GetValues().ToList();
            foreach (var id in ids)
            {
                int n = (int)id;
                string hex = "0x" + n.ToString("X4");
                snap.SupportedCapIds.Add(hex);
                if (n >= 0x8000) snap.CustomCapIds.Add(hex);
            }
        }

        private static void ProbePixelAndBitDepth(DataSource source, Fi6800CapabilitySnapshot snap)
        {
            var caps = source.Capabilities;

            if (caps.ICapPixelType.IsSupported)
                snap.PixelTypes = caps.ICapPixelType.GetValues().ToList();

            if (caps.ICapBitDepth.IsSupported)
                snap.BitDepths = caps.ICapBitDepth.GetValues().ToList();
        }

        private static void ProbeResolution(DataSource source, Fi6800CapabilitySnapshot snap)
        {
            var caps = source.Capabilities;
            if (!caps.ICapXResolution.IsSupported) return;

            var values = caps.ICapXResolution.GetValues().ToList();
            snap.ResolutionValues = values.Select(f => (float)f).OrderBy(f => f).ToList();
            if (snap.ResolutionValues.Count > 0)
            {
                snap.ResolutionMin = snap.ResolutionValues.First();
                snap.ResolutionMax = snap.ResolutionValues.Last();
            }
        }

        private static void ProbeGeometry(DataSource source, Fi6800CapabilitySnapshot snap)
        {
            var caps = source.Capabilities;

            if (caps.ICapSupportedSizes.IsSupported)
                snap.SupportedSizes = caps.ICapSupportedSizes.GetValues().ToList();

            if (caps.ICapPhysicalWidth.IsSupported)
            {
                try { snap.PhysicalWidthInches = (float)caps.ICapPhysicalWidth.GetCurrent(); } catch { }
            }
            if (caps.ICapPhysicalHeight.IsSupported)
            {
                try { snap.PhysicalHeightInches = (float)caps.ICapPhysicalHeight.GetCurrent(); } catch { }

                // Heurística long page: si reporta más de 50" de altura física, asumir soporte
                if (snap.PhysicalHeightInches.HasValue && snap.PhysicalHeightInches.Value > 50f)
                    snap.Features.LongPageDetected = true;
            }
        }

        private static void ProbeCompressionAndFormat(DataSource source, Fi6800CapabilitySnapshot snap)
        {
            var caps = source.Capabilities;

            if (caps.ICapCompression.IsSupported)
                snap.Compressions = caps.ICapCompression.GetValues().ToList();

            if (caps.ICapImageFileFormat.IsSupported)
                snap.FileFormats = caps.ICapImageFileFormat.GetValues().ToList();
        }

        private static void ProbePatchCodes(DataSource source, Fi6800CapabilitySnapshot snap)
        {
            var caps = source.Capabilities;
            if (caps.ICapSupportedPatchCodeTypes.IsSupported)
                snap.PatchCodes = caps.ICapSupportedPatchCodeTypes.GetValues().ToList();
        }

        private static void ProbeBarcodes(DataSource source, Fi6800CapabilitySnapshot snap)
        {
            var caps = source.Capabilities;
            if (caps.ICapSupportedBarcodeTypes.IsSupported)
                snap.Barcodes = caps.ICapSupportedBarcodeTypes.GetValues().ToList();
        }

        private static void ProbeExtImageInfo(DataSource source, Fi6800CapabilitySnapshot snap)
        {
            var caps = source.Capabilities;
            if (caps.ICapSupportedExtImageInfo.IsSupported)
                snap.ExtImageInfo = caps.ICapSupportedExtImageInfo.GetValues().ToList();
        }

        private static void ProbeFeatures(DataSource source, Fi6800CapabilitySnapshot snap)
        {
            var c = source.Capabilities;
            var f = snap.Features;

            f.Duplex                = c.CapDuplex.IsSupported;
            f.AutoColor             = c.ICapAutomaticColorEnabled.IsSupported;
            f.AutoDeskew            = c.ICapAutomaticDeskew.IsSupported;
            f.AutoRotate            = c.ICapAutomaticRotate.IsSupported;
            f.AutoBorder            = c.ICapAutomaticBorderDetection.IsSupported;
            f.AutoLengthDetection   = c.ICapAutomaticLengthDetection.IsSupported;
            f.DiscardBlank          = c.ICapAutoDiscardBlankPages.IsSupported;
            f.Imprinter             = c.CapPrinter.IsSupported;
            f.PatchDetection        = c.ICapPatchCodeDetectionEnabled.IsSupported;
            f.BarcodeDetection      = c.ICapBarcodeDetectionEnabled.IsSupported;
            f.JobControl            = c.CapJobControl.IsSupported;
            f.CustomDsData          = c.CapCustomDSData.IsSupported;
            f.ExtImageInfo          = c.ICapExtImageInfo.IsSupported;
            f.Threshold             = c.ICapThreshold.IsSupported;
            f.Brightness            = c.ICapBrightness.IsSupported;
            f.Contrast              = c.ICapContrast.IsSupported;
            f.Gamma                 = c.ICapGamma.IsSupported;
            f.Filter                = c.ICapFilter.IsSupported;
            f.JpegQuality           = c.ICapJpegQuality.IsSupported;
            f.JpegSubsampling       = c.ICapJpegSubsampling.IsSupported;
            f.Mirror                = c.ICapMirror.IsSupported;
            f.Rotation              = c.ICapRotation.IsSupported;
            f.Orientation           = c.ICapOrientation.IsSupported;

            // Multifeed
            try
            {
                if (c.CapDoubleFeedDetection.IsSupported)
                {
                    f.DoubleFeedDetection = true;
                    var values = c.CapDoubleFeedDetection.GetValues().ToList();
                    f.DoubleFeedByLength     = values.Any(v => v == DoubleFeedDetection.ByLength);
                    f.DoubleFeedByUltrasonic = values.Any(v => v == DoubleFeedDetection.Ultrasonic);
                }
            }
            catch { /* graceful degradation */ }

            // Multi-stream heurístico
            try
            {
                f.MultiStreamDetected = c.CapCameraEnabled.IsSupported && c.CapCameraSide.IsSupported;
            }
            catch { }
        }

        private static void ProbeCustomCaps(DataSource source, Fi6800CapabilitySnapshot snap)
        {
            // Para cada cap custom (ID >= 0x8000), registramos solo el ID
            // La semántica no está publicada por PFU/Ricoh.
            foreach (var hexId in snap.CustomCapIds)
            {
                int n = Convert.ToInt32(hexId.Substring(2), 16);
                snap.CustomCaps.Add(new CustomCapDescriptor
                {
                    Id = hexId,
                    IdNumeric = n,
                    Notes = "Cap custom de PaperStream — semántica no publicada por PFU/Ricoh"
                });
            }
        }
    }
}
