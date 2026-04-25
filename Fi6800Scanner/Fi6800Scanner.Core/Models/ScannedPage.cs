using System;
using System.Collections.Generic;
using System.Drawing;
using NTwain.Data;

namespace Fi6800Scanner.Core.Models
{
    public class ScannedPage : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int PageNumber { get; set; }
        public PageSide Side { get; set; }
        public PixelType PixelType { get; set; }
        public int WidthPx { get; set; }
        public int HeightPx { get; set; }
        public int DpiX { get; set; }
        public int DpiY { get; set; }
        public string TempFilePath { get; set; }
        public Bitmap Thumbnail { get; set; }
        public Bitmap FullImage { get; set; }
        public PatchCode? DetectedPatch { get; set; }
        public IReadOnlyList<DetectedBarcode> Barcodes { get; set; } = Array.Empty<DetectedBarcode>();
        public double SkewAngleDegrees { get; set; }
        public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

        public void Dispose()
        {
            Thumbnail?.Dispose();
            Thumbnail = null;
            FullImage?.Dispose();
            FullImage = null;

            if (!string.IsNullOrEmpty(TempFilePath) && System.IO.File.Exists(TempFilePath))
            {
                try { System.IO.File.Delete(TempFilePath); } catch { /* best effort */ }
            }
        }
    }
}
