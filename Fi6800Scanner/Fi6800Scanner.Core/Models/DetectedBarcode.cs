using System.Drawing;
using NTwain.Data;

namespace Fi6800Scanner.Core.Models
{
    public class DetectedBarcode
    {
        public BarcodeType Type { get; set; }
        public string Text { get; set; }
        public Point Position { get; set; }
        public uint Confidence { get; set; }
    }
}
